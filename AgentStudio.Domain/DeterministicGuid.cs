using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Domain;

/// <summary>Stable Guid derived from a string (SHA-256, first 16 bytes) — for entities defined
/// in appsettings.json (config-based providers, users) that need a consistent Id across process
/// restarts without a place to persist a randomly generated one.</summary>
public static class DeterministicGuid
{
    public static Guid From(string seed) => new(SHA256.HashData(Encoding.UTF8.GetBytes(seed))[..16]);
}
