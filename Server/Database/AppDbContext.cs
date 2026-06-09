using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Server.Entities.PreKeys;
using Server.Entities.Transfers;

namespace Server.Database
{
    public class AppDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>
    {
        public AppDbContext(DbContextOptions<AppDbContext> options)
            : base(options)
        {
        }

        public DbSet<SignedPreKey> SignedPreKeys => Set<SignedPreKey>();
        public DbSet<OneTimePreKey> OneTimePreKeys => Set<OneTimePreKey>();

        public DbSet<FileTransfer> FileTransfers => Set<FileTransfer>();
        public DbSet<FileTransferItem> FileTransferItems => Set<FileTransferItem>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<ApplicationUser>(entity =>
            {
                entity.Property(user => user.PayloadSalt)
                    .IsRequired();

                entity.Property(user => user.EncryptedKeyPayload)
                    .IsRequired();

                entity.Property(user => user.X25519PublicKey)
                    .IsRequired();

                entity.Property(user => user.Ed25519PublicKey)
                    .IsRequired();

                entity.Property(user => user.CreatedAtUtc)
                    .IsRequired();
            });

            modelBuilder.Entity<SignedPreKey>(entity =>
            {
                entity.HasKey(preKey => preKey.Id);

                entity.HasOne(preKey => preKey.User)
                    .WithMany(user => user.SignedPreKeys)
                    .HasForeignKey(preKey => preKey.UserId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasIndex(preKey => preKey.UserId)
                   .IsUnique();

                entity.HasIndex(preKey => new { preKey.UserId, preKey.KeyId })
                    .IsUnique();

                entity.Property(preKey => preKey.PublicKey)
                    .IsRequired();

                entity.Property(preKey => preKey.Signature)
                    .IsRequired();

                entity.Property(preKey => preKey.CreatedAtUtc)
                    .IsRequired();

            });

            modelBuilder.Entity<OneTimePreKey>(entity =>
            {
                entity.HasKey(preKey => preKey.Id);

                entity.HasOne(preKey => preKey.User)
                    .WithMany(user => user.OneTimePreKeys)
                    .HasForeignKey(preKey => preKey.UserId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasIndex(preKey => new { preKey.UserId, preKey.KeyId })
                    .IsUnique();

                entity.HasIndex(preKey => new { preKey.UserId, preKey.IsClaimed });

                entity.Property(preKey => preKey.PublicKey)
                    .IsRequired();

                entity.Property(preKey => preKey.CreatedAtUtc)
                    .IsRequired();

                entity.Property(preKey => preKey.IsClaimed)
                    .IsRequired();
            });

            modelBuilder.Entity<FileTransfer>(entity =>
            {
                entity.HasKey(transfer => transfer.Id);

                entity.HasOne(transfer => transfer.Sender)
                    .WithMany(user => user.SentTransfers)
                    .HasForeignKey(transfer => transfer.SenderId)
                    .OnDelete(DeleteBehavior.Restrict);

                entity.HasOne(transfer => transfer.Recipient)
                    .WithMany(user => user.ReceivedTransfers)
                    .HasForeignKey(transfer => transfer.RecipientId)
                    .OnDelete(DeleteBehavior.Restrict);

                entity.HasMany(transfer => transfer.Files)
                    .WithOne(file => file.Transfer)
                    .HasForeignKey(file => file.TransferId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasIndex(transfer => transfer.RecipientId);

                entity.HasIndex(transfer => transfer.SenderId);

                entity.HasIndex(transfer => transfer.ExpiresAtUtc);

                entity.HasIndex(transfer => transfer.Status);

                entity.Property(transfer => transfer.X3dhHeader)
                    .IsRequired();

                entity.Property(transfer => transfer.CreatedAtUtc)
                    .IsRequired();

                entity.Property(transfer => transfer.ExpiresAtUtc)
                    .IsRequired();

                entity.Property(transfer => transfer.Status)
                    .IsRequired();
            });

            modelBuilder.Entity<FileTransferItem>(entity =>
            {
                entity.HasKey(file => file.Id);

                entity.HasIndex(file => new { file.TransferId, file.FileIndex })
                    .IsUnique();

                entity.Property(file => file.FileHeader)
                    .IsRequired();

                entity.Property(file => file.StorageObjectName)
                    .IsRequired()
                    .HasMaxLength(200);

                entity.Property(file => file.CiphertextLength)
                    .IsRequired();

                entity.Property(file => file.CreatedAtUtc)
                    .IsRequired();

                entity.Property(file => file.IsUploaded)
                    .IsRequired();
            });
        }
    }
}
