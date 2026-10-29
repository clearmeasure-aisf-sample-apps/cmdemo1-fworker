using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fworker.Contracts.Ids;

// W6.1: the JSON form of the ids. ItemId and RepoId are read-only structs with constructors that validate and lowercase, which System.Text.Json cannot call for a struct with properties it cannot set: a
// message or a saved state that carried one came back as the default id ("/#0"). Whatever persists a message (Wolverine's durable inbox and outbox after a restart, scheduled messages) or a state with an
// id in it needs the id back. The form is the text people read, "owner/repo#N" and "owner/repo"; the reader also takes the object form the default serializer wrote before, so nothing already stored is lost.

public sealed class RepoIdJsonConverter : JsonConverter<RepoId>
{
    public override RepoId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return RepoId.TryParse(reader.GetString(), out var id) ? id : throw new JsonException($"'{reader.GetString()}' is not 'owner/repo'");
        if (reader.TokenType == JsonTokenType.StartObject) return ReadObject(ref reader);
        throw new JsonException("a repository is a string 'owner/repo'");
    }

    // {"Owner":"o","Repo":"r"}, in any case of the names
    internal static RepoId ReadObject(ref Utf8JsonReader reader)
    {
        string? owner = null, repo = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString(); reader.Read();
            if (string.Equals(name, "owner", StringComparison.OrdinalIgnoreCase)) owner = reader.GetString();
            else if (string.Equals(name, "repo", StringComparison.OrdinalIgnoreCase)) repo = reader.GetString();
            else reader.Skip();
        }
        return RepoId.TryParse($"{owner}/{repo}", out var id) ? id : throw new JsonException("a repository needs an owner and a repo");
    }

    public override void Write(Utf8JsonWriter writer, RepoId value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}

public sealed class ItemIdJsonConverter : JsonConverter<ItemId>
{
    public override ItemId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return ItemId.TryParse(reader.GetString(), out var id) ? id : throw new JsonException($"'{reader.GetString()}' is not 'owner/repo#N'");
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            // {"Repository":{"Owner":"o","Repo":"r"},"Number":7}
            RepoId? repo = null; int? number = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var name = reader.GetString(); reader.Read();
                if (string.Equals(name, "repository", StringComparison.OrdinalIgnoreCase)) repo = reader.TokenType == JsonTokenType.String && RepoId.TryParse(reader.GetString(), out var r) ? r : RepoIdJsonConverter.ReadObject(ref reader);
                else if (string.Equals(name, "number", StringComparison.OrdinalIgnoreCase)) number = reader.GetInt32();
                else reader.Skip();
            }
            if (repo is { } rp && number is { } n && n >= 1) return new ItemId(rp, n);
            throw new JsonException("an item needs a repository and a number");
        }
        throw new JsonException("an item is a string 'owner/repo#N'");
    }

    public override void Write(Utf8JsonWriter writer, ItemId value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}
