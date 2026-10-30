using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Server.Controllers;
using Server.Database;
using Server.Entities.PreKeys;
using Server.Interfaces;
using Server.Options;
using Shared.DTO.Transfers;
using System.Security.Claims;

namespace Tests.Server;

public class TransferSizeLimitTests
{
    [Theory]
    [InlineData(536870912L, 10737418240L, true)]
    [InlineData(10737418240L, 10737418240L, true)]
    [InlineData(10737418241L, 10737418240L, false)]
    [InlineData(1025L, 1024L, false)]
    public async Task CreateTransfer_UsesConfiguredLimit(long length, long limit, bool accepted)
    {
        await using var fixture = await Fixture.CreateAsync(limit);
        var result = await fixture.Controller.CreateTransfer(new CreateTransferRequest
        {
            RecipientUsername = "bob",
            X3dhHeader = [1],
            Files = [new() { FileIndex = 0, FileHeader = [1], CiphertextLength = length }]
        });

        if (accepted)
        {
            var created = Assert.IsType<CreatedResult>(result.Result);
            Assert.IsType<CreateTransferResponse>(created.Value);
            Assert.Equal(length, (await fixture.Db.FileTransferItems.SingleAsync()).CiphertextLength);
        }
        else
        {
            Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Empty(await fixture.Db.FileTransfers.ToListAsync());
        }
    }

    [Theory]
    [InlineData(null, 411)]
    [InlineData(3L, 400)]
    [InlineData(4L, 204)]
    public async Task Upload_RequiresExactLength_AndSetsRequestBodyLimit(long? contentLength, int status)
    {
        await using var fixture = await Fixture.CreateAsync(1024);
        var created = await fixture.Controller.CreateTransfer(new CreateTransferRequest
        {
            RecipientUsername = "bob",
            X3dhHeader = [1],
            Files = [new() { FileIndex = 0, FileHeader = [1], CiphertextLength = 4 }]
        });
        var transfer = Assert.IsType<CreateTransferResponse>(Assert.IsType<CreatedResult>(created.Result).Value);
        var feature = new BodySizeFeature();
        fixture.Controller.HttpContext.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);
        fixture.Controller.Request.ContentLength = contentLength;
        fixture.Controller.Request.Body = new MemoryStream([1, 2, 3, 4]);

        var result = await fixture.Controller.UploadFileContent(
            transfer.TransferId, transfer.Files.Single().FileId, CancellationToken.None);

        Assert.Equal(status, Assert.IsAssignableFrom<IStatusCodeActionResult>(result).StatusCode);
        Assert.Equal(status == 204, fixture.Storage.WasSaved);
        if (status == 204)
            Assert.Equal(4L, feature.MaxRequestBodySize);
    }

    private sealed class BodySizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; } = 30000000;
    }

    private sealed class RecordingStorage : ITransferStorageService
    {
        public bool WasSaved { get; private set; }
        public string GetStorageObjectName(Guid recipientId, Guid senderId, Guid transferId, Guid fileId) => $"{fileId}.bin";
        public async Task SaveAsync(string name, Stream content, CancellationToken token)
        {
            await content.CopyToAsync(Stream.Null, token);
            WasSaved = true;
        }
        public Task<Stream> OpenReadAsync(string name, CancellationToken token) => throw new NotSupportedException();
        public Task DeleteAsync(string name, CancellationToken token) => Task.CompletedTask;
        public Task DeleteTransferDirectoryAsync(Guid recipientId, Guid senderId, Guid transferId, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _provider;
        public AppDbContext Db { get; }
        public RecordingStorage Storage { get; } = new();
        public TransfersController Controller { get; }

        private Fixture(SqliteConnection connection, ServiceProvider provider, long limit, Guid senderId)
        {
            _connection = connection;
            _provider = provider;
            Db = provider.GetRequiredService<AppDbContext>();
            Controller = new TransfersController(Db, provider.GetRequiredService<UserManager<ApplicationUser>>(),
                Storage, NullLogger<TransfersController>.Instance,
                Options.Create(new TransferOptions { MaximumCiphertextLength = limit }))
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(
                            [new Claim(ClaimTypes.NameIdentifier, senderId.ToString())], "test"))
                    }
                }
            };
        }

        public static async Task<Fixture> CreateAsync(long limit)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));
            services.AddIdentityCore<ApplicationUser>().AddEntityFrameworkStores<AppDbContext>();
            var provider = services.BuildServiceProvider();
            var db = provider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();
            var alice = new ApplicationUser { Id = Guid.NewGuid(), UserName = "alice", NormalizedUserName = "ALICE" };
            db.Users.AddRange(alice, new ApplicationUser { Id = Guid.NewGuid(), UserName = "bob", NormalizedUserName = "BOB" });
            await db.SaveChangesAsync();
            return new Fixture(connection, provider, limit, alice.Id);
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
