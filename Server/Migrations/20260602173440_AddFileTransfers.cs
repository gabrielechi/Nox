using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Server.Migrations
{
    /// <inheritdoc />
    public partial class AddFileTransfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FileTransfers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SenderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecipientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    X3dhHeader = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DownloadedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileTransfers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FileTransfers_AspNetUsers_RecipientId",
                        column: x => x.RecipientId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FileTransfers_AspNetUsers_SenderId",
                        column: x => x.SenderId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FileTransferItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TransferId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    FileHeader = table.Column<byte[]>(type: "BLOB", nullable: false),
                    StorageObjectName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CiphertextLength = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileTransferItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FileTransferItems_FileTransfers_TransferId",
                        column: x => x.TransferId,
                        principalTable: "FileTransfers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FileTransferItems_TransferId_FileIndex",
                table: "FileTransferItems",
                columns: new[] { "TransferId", "FileIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FileTransfers_ExpiresAtUtc",
                table: "FileTransfers",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_FileTransfers_RecipientId",
                table: "FileTransfers",
                column: "RecipientId");

            migrationBuilder.CreateIndex(
                name: "IX_FileTransfers_SenderId",
                table: "FileTransfers",
                column: "SenderId");

            migrationBuilder.CreateIndex(
                name: "IX_FileTransfers_Status",
                table: "FileTransfers",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FileTransferItems");

            migrationBuilder.DropTable(
                name: "FileTransfers");
        }
    }
}
