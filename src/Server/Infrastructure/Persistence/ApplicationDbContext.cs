using RemoteSupport.Server.Application.Interfaces;
using RemoteSupport.Server.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace RemoteSupport.Server.Infrastructure.Persistence;

public class ApplicationDbContext : DbContext, IApplicationDbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<SupportCode> SupportCodes => Set<SupportCode>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<SessionEvent> SessionEvents => Set<SessionEvent>();
    public DbSet<FileTransferMetadata> FileTransfers => Set<FileTransferMetadata>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<CustomerSessionToken> CustomerSessionTokens => Set<CustomerSessionToken>();
    public DbSet<TransportToken> TransportTokens => Set<TransportToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Username).IsUnique();
            entity.HasIndex(e => e.Email).IsUnique();
            entity.Property(e => e.Username).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Email).HasMaxLength(256).IsRequired();
            entity.Property(e => e.PasswordHash).HasMaxLength(512).IsRequired();
            entity.Property(e => e.DisplayName).HasMaxLength(200);
        });

        modelBuilder.Entity<Device>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.DeviceIdentifier).IsUnique();
            entity.Property(e => e.DeviceIdentifier).HasMaxLength(200).IsRequired();
            entity.Property(e => e.DeviceName).HasMaxLength(200);
            entity.Property(e => e.OperatingSystem).HasMaxLength(100);
            entity.Property(e => e.OsVersion).HasMaxLength(50);
        });

        modelBuilder.Entity<SupportCode>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Code).IsUnique();
            entity.Property(e => e.Code).HasMaxLength(20).IsRequired();
            entity.Property(e => e.DeviceIdentifier).HasMaxLength(200).IsRequired();
        });

        modelBuilder.Entity<Session>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.PeerConnectionId);
            entity.HasIndex(e => e.RelayConnectionId);
            entity.Property(e => e.CustomerDeviceIdentifier).HasMaxLength(200).IsRequired();
            entity.Property(e => e.CustomerDeviceName).HasMaxLength(200);
            entity.Property(e => e.CustomerOperatingSystem).HasMaxLength(100);
            entity.Property(e => e.EndReason).HasMaxLength(500);

            entity.HasOne(e => e.AgentUser)
                .WithMany(u => u.AgentSessions)
                .HasForeignKey(e => e.AgentUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SessionEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Details).HasMaxLength(2000);

            entity.HasOne(e => e.Session)
                .WithMany(s => s.Events)
                .HasForeignKey(e => e.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FileTransferMetadata>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FileName).HasMaxLength(500).IsRequired();
            entity.Property(e => e.FailureReason).HasMaxLength(1000);

            entity.HasOne(e => e.Session)
                .WithMany(s => s.FileTransfers)
                .HasForeignKey(e => e.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Description).HasMaxLength(2000);
            entity.Property(e => e.IpAddress).HasMaxLength(45);
            entity.Property(e => e.UserAgent).HasMaxLength(500);

            entity.HasOne(e => e.User)
                .WithMany(u => u.AuditLogs)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TokenHash).IsUnique();
            entity.HasIndex(e => e.UserId);
            entity.Property(e => e.TokenHash).HasMaxLength(512).IsRequired();
            entity.Property(e => e.DeviceIdentifier).HasMaxLength(200);
            entity.Property(e => e.ReplacedByTokenHash).HasMaxLength(512);
            entity.Property(e => e.RevokedByIp).HasMaxLength(45);

            entity.HasOne(e => e.User)
                .WithMany(u => u.RefreshTokens)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CustomerSessionToken>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TokenHash).IsUnique();
            entity.Property(e => e.TokenHash).HasMaxLength(512).IsRequired();
            entity.Property(e => e.DeviceIdentifier).HasMaxLength(200).IsRequired();
            entity.Property(e => e.DeviceName).HasMaxLength(200);
            entity.Property(e => e.OperatingSystem).HasMaxLength(100);

            entity.HasOne(e => e.LinkedSession)
                .WithMany()
                .HasForeignKey(e => e.LinkedSessionId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<TransportToken>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.SessionId);
            entity.HasIndex(e => e.TokenHash);
            entity.Property(e => e.TokenHash).HasMaxLength(512).IsRequired();
            entity.Property(e => e.ConnectionType).HasMaxLength(50);

            entity.HasOne(e => e.Session)
                .WithMany()
                .HasForeignKey(e => e.SessionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
