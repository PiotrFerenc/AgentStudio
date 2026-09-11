using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AgentStudio.Infrastructure;

public sealed class AgentStudioDbContext : DbContext
{
    public AgentStudioDbContext(DbContextOptions<AgentStudioDbContext> options) : base(options) { }

    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<AgentVersion> AgentVersions => Set<AgentVersion>();
    public DbSet<ExecutionLog> ExecutionLogs => Set<ExecutionLog>();
    public DbSet<ConversationState> Conversations => Set<ConversationState>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentChunk> DocumentChunks => Set<DocumentChunk>();
    public DbSet<AgentCollectionEntry> AgentCollectionEntries => Set<AgentCollectionEntry>();
    public DbSet<GraphComponent> GraphComponents => Set<GraphComponent>();
    public DbSet<AgentCollaborator> AgentCollaborators => Set<AgentCollaborator>();

    /// <summary>
    /// Structural equality/clone for a jsonb-converted collection property. Required whenever an
    /// existing tracked entity's collection is *mutated in place* (not replaced) before
    /// SaveChanges — without this, EF's default reference-equality comparer sees the same List/
    /// Dictionary reference as "unchanged" and silently skips the UPDATE.
    /// </summary>
    private static ValueComparer<T> JsonValueComparer<T>() => new(
        (a, b) => JsonSerializer.Serialize(a, AgentStudioJson.Options) == JsonSerializer.Serialize(b, AgentStudioJson.Options),
        c => JsonSerializer.Serialize(c, AgentStudioJson.Options).GetHashCode(),
        c => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(c, AgentStudioJson.Options), AgentStudioJson.Options)!);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var stepsConverter = new ValueConverter<List<ExecutionStep>, string>(
            s => JsonSerializer.Serialize(s, AgentStudioJson.Options),
            s => JsonSerializer.Deserialize<List<ExecutionStep>>(s, AgentStudioJson.Options) ?? new List<ExecutionStep>());

        var messagesConverter = new ValueConverter<List<ChatMessage>, string>(
            s => JsonSerializer.Serialize(s, AgentStudioJson.Options),
            s => JsonSerializer.Deserialize<List<ChatMessage>>(s, AgentStudioJson.Options) ?? new List<ChatMessage>());

        var variablesConverter = new ValueConverter<Dictionary<string, string>, string>(
            s => JsonSerializer.Serialize(s, AgentStudioJson.Options),
            s => JsonSerializer.Deserialize<Dictionary<string, string>>(s, AgentStudioJson.Options) ?? new Dictionary<string, string>());

        var embeddingConverter = new ValueConverter<List<float>, string>(
            s => JsonSerializer.Serialize(s, AgentStudioJson.Options),
            s => JsonSerializer.Deserialize<List<float>>(s, AgentStudioJson.Options) ?? new List<float>());

        modelBuilder.Entity<Agent>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.Name).HasMaxLength(200).IsRequired();
            e.Ignore(a => a.Draft);
            e.HasMany(a => a.Versions).WithOne().HasForeignKey(v => v.AgentId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(a => a.Collaborators).WithOne().HasForeignKey(c => c.AgentId).OnDelete(DeleteBehavior.Cascade);
            e.Property(a => a.EnvironmentVariables).HasConversion(variablesConverter, JsonValueComparer<Dictionary<string, string>>());
        });

        modelBuilder.Entity<AgentCollaborator>(e =>
        {
            e.HasKey(c => new { c.AgentId, c.UserId });
        });

        modelBuilder.Entity<AgentVersion>(e =>
        {
            e.HasKey(v => v.Id);
            e.Property(v => v.Id).ValueGeneratedOnAdd();
            e.Ignore(v => v.Graph);
            e.Ignore(v => v.FormFields);
        });

        modelBuilder.Entity<ExecutionLog>(e =>
        {
            e.HasKey(l => l.Id);
            e.HasIndex(l => l.ExecutionId).IsUnique();
            e.Property(l => l.Steps).HasConversion(stepsConverter, JsonValueComparer<List<ExecutionStep>>());
        });

        modelBuilder.Entity<ConversationState>(e =>
        {
            e.HasKey(c => c.ConversationId);
            e.Property(c => c.ConversationId).HasMaxLength(64);
            e.Property(c => c.Messages).HasConversion(messagesConverter, JsonValueComparer<List<ChatMessage>>());
            e.Property(c => c.Variables).HasConversion(variablesConverter, JsonValueComparer<Dictionary<string, string>>());
            e.HasIndex(c => new { c.AgentId, c.AgentVersion });
        });

        modelBuilder.Entity<Document>(e =>
        {
            e.HasKey(d => d.Id);
            e.HasIndex(d => d.AgentId);
            e.Property(d => d.FileName).HasMaxLength(260).IsRequired();
        });

        modelBuilder.Entity<DocumentChunk>(e =>
        {
            e.HasKey(c => c.Id);
            e.HasIndex(c => c.DocumentId);
            e.HasIndex(c => c.AgentId);
            e.Property(c => c.Embedding).HasConversion(embeddingConverter, JsonValueComparer<List<float>>());
        });

        modelBuilder.Entity<AgentCollectionEntry>(e =>
        {
            e.HasKey(c => new { c.AgentId, c.Key });
            e.Property(c => c.Key).HasMaxLength(200);
            e.HasOne<Agent>().WithMany().HasForeignKey(c => c.AgentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GraphComponent>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.Name).HasMaxLength(200).IsRequired();
        });

        // SQLite's EF provider refuses to translate ORDER BY/WHERE comparisons on a native
        // DateTimeOffset column at all (not a value quirk — a blanket translation restriction).
        // Store every DateTimeOffset/DateTimeOffset? column as UTC ticks (a plain long) instead —
        // ordinary numeric comparisons translate fine, and the CLR-side type stays DateTimeOffset
        // everywhere else in the app. Applied globally so a newly added DateTimeOffset property
        // is covered automatically, the same "can't be forgotten" discipline as the JSON
        // converter/GraphMapper cases elsewhere in this codebase.
        var dateTimeOffsetConverter = new ValueConverter<DateTimeOffset, long>(
            d => d.UtcTicks,
            t => new DateTimeOffset(t, TimeSpan.Zero));
        var nullableDateTimeOffsetConverter = new ValueConverter<DateTimeOffset?, long?>(
            d => d.HasValue ? d.Value.UtcTicks : null,
            t => t.HasValue ? new DateTimeOffset(t.Value, TimeSpan.Zero) : null);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset))
                    property.SetValueConverter(dateTimeOffsetConverter);
                else if (property.ClrType == typeof(DateTimeOffset?))
                    property.SetValueConverter(nullableDateTimeOffsetConverter);
            }
        }
    }
}

public static class DomainJson
{
    [System.Obsolete("Use AgentStudioJson.Options from AgentStudio.Domain.")]
    public static readonly JsonSerializerOptions Options = AgentStudioJson.Options;
}
