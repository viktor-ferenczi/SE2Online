using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Y51_SE2Online.Sync;

public enum GridSharing { Private, Faction, Everyone }
public enum SocialAction
{
    Refresh, CreateFaction, RequestJoin, Invite, AcceptInvitation, ApproveRequest,
    RejectRequest, LeaveFaction, RemoveMember, TransferLeadership, DisbandFaction,
    ClaimGrid, TransferGrid, ReleaseGrid, ShareGrid,
    /// <summary>Suit broadcast: Offset 1 on, 0 off.</summary>
    Broadcast
}

public sealed record SocialRequest(SocialAction Action, string FactionId = "", long Player = 0,
    uint Grid = 0, GridSharing Sharing = GridSharing.Private, string Name = "", string Tag = "", int Offset = 0);
public sealed record SocialPlayer(long Identity, string Name);
public sealed record SocialGrid(uint NetId, string Name, long Owner, GridSharing Sharing);
public sealed class OnlineFaction
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Tag { get; set; } = "";
    public long Leader { get; set; }
    public List<long> Members { get; set; } = new();
    public List<long> Invitations { get; set; } = new();
    public List<long> Requests { get; set; } = new();
}

public sealed class SocialSnapshot
{
    public long Revision { get; set; }
    public long Actor { get; set; }
    public string Message { get; set; } = "";
    public SocialPlayer[] Players { get; set; } = Array.Empty<SocialPlayer>();
    public OnlineFaction[] Factions { get; set; } = Array.Empty<OnlineFaction>();
    public SocialGrid[] Grids { get; set; } = Array.Empty<SocialGrid>();
    public int TotalGrids { get; set; }
    public int Offset { get; set; }
}

public sealed class SocialState
{
    public int Version { get; set; } = 1;
    public long Revision { get; set; }
    public List<SocialPlayer> Players { get; set; } = new();
    public List<OnlineFaction> Factions { get; set; } = new();

    public static bool CanAccess(long owner, long actor, GridSharing sharing, IEnumerable<OnlineFaction> factions)
    {
        if (!PlayerIdentities.IsHuman(actor)) return false;
        if (owner == actor) return true;
        if (PlayerIdentities.IsNpc(owner)) return false;
        if (sharing == GridSharing.Everyone) return true;
        return sharing == GridSharing.Faction && owner != 0 && factions.Any(f => f.Members.Contains(owner) && f.Members.Contains(actor));
    }

    public OnlineFaction FactionOf(long actor) => Factions.FirstOrDefault(f => f.Members.Contains(actor));
    public static bool CanManageGrid(long owner, long actor) => PlayerIdentities.IsHuman(actor) && (owner == actor || owner == 0);
    public void Register(long identity, string name)
    {
        if (!PlayerIdentities.IsHuman(identity)) return;
        name = Clean(name, 64);
        int index = Players.FindIndex(p => p.Identity == identity);
        if (index >= 0 && Players[index].Name == name) return;
        if (index >= 0) Players[index] = new(identity, name);
        else Players.Add(new(identity, name));
        Revision++;
    }

    public string Apply(long actor, SocialRequest request)
    {
        if (!PlayerIdentities.IsHuman(actor) || !Players.Any(p => p.Identity == actor)) return "Your player identity is not ready.";
        if (!Enum.IsDefined(request.Action)) return "Unknown action.";
        var own = FactionOf(actor);
        var faction = Factions.FirstOrDefault(f => f.Id == request.FactionId);
        if (request.Action == SocialAction.CreateFaction)
        {
            if (own != null) return "Leave your current faction first.";
            string name = Clean(request.Name, 48), tag = Clean(request.Tag, 5).ToUpperInvariant();
            if (name.Length < 3 || tag.Length < 2 || !tag.All(char.IsAsciiLetterOrDigit)) return "Use a name of 3-48 characters and a tag of 2-5 letters or digits.";
            if (Factions.Count >= 256) return "This world has reached its faction limit.";
            if (Factions.Any(f => f.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase) || f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return "That faction name or tag is already in use.";
            ClearPending(actor);
            Factions.Add(new OnlineFaction { Id = Guid.NewGuid().ToString("N"), Name = name, Tag = tag, Leader = actor, Members = new() { actor } });
            Revision++; return "Faction created.";
        }
        if (request.Action == SocialAction.LeaveFaction)
        {
            if (own == null) return "You are not in a faction.";
            if (own.Leader == actor && own.Members.Count > 1) return "Transfer leadership before leaving.";
            own.Members.Remove(actor);
            if (own.Members.Count == 0) Factions.Remove(own);
            Revision++; return "You left the faction.";
        }
        if (faction == null) return "That faction no longer exists.";
        if (request.Action == SocialAction.RequestJoin)
        {
            if (own != null) return "Leave your current faction first.";
            if (!faction.Requests.Contains(actor)) { faction.Requests.Add(actor); Revision++; }
            return "Join request sent to the faction leader.";
        }
        if (request.Action == SocialAction.AcceptInvitation)
        {
            if (own != null) return "Leave your current faction first.";
            if (!faction.Invitations.Contains(actor)) return "You have not been invited to that faction.";
            ClearPending(actor); faction.Members.Add(actor); Revision++; return "You joined the faction.";
        }
        if (faction.Leader != actor) return "Only the faction leader can do that.";
        bool known = Players.Any(p => p.Identity == request.Player);
        switch (request.Action)
        {
            case SocialAction.Invite:
                if (!known || FactionOf(request.Player) != null) return "Choose a player who is not already in a faction.";
                if (!faction.Invitations.Contains(request.Player)) faction.Invitations.Add(request.Player);
                break;
            case SocialAction.ApproveRequest:
                if (!known || !faction.Requests.Contains(request.Player) || FactionOf(request.Player) != null) return "That player has no eligible join request.";
                ClearPending(request.Player); faction.Members.Add(request.Player); break;
            case SocialAction.RejectRequest:
                if (!faction.Requests.Remove(request.Player)) return "That join request is no longer pending.";
                break;
            case SocialAction.RemoveMember:
                if (request.Player == actor) return "Use Leave faction to leave.";
                if (!faction.Members.Remove(request.Player)) return "Choose a faction member.";
                break;
            case SocialAction.TransferLeadership:
                if (request.Player == actor || !faction.Members.Contains(request.Player)) return "Choose another faction member.";
                faction.Leader = request.Player; break;
            case SocialAction.DisbandFaction:
                Factions.Remove(faction); break;
            default: return "Unknown faction action.";
        }
        Revision++; return "Faction updated.";
    }

    private void ClearPending(long player)
    {
        foreach (var faction in Factions) { faction.Requests.Remove(player); faction.Invitations.Remove(player); }
    }

    public static string Clean(string value, int limit) => new string((value ?? "").Where(c => !char.IsControl(c)).Take(limit).ToArray()).Trim();
    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this);
    public static SocialState Deserialize(ReadOnlySpan<byte> data)
    {
        var state = JsonSerializer.Deserialize<SocialState>(data) ?? throw new InvalidOperationException("Empty faction save.");
        state.Validate(); return state;
    }
    public void Validate()
    {
        if (Version != 1 || Players == null || Factions == null || Players.Count > 10000 || Factions.Count > 256) throw new InvalidOperationException("Invalid faction save schema.");
        var players = new HashSet<long>(); var members = new HashSet<long>(); var ids = new HashSet<string>(); var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var player in Players)
            if (player == null || !PlayerIdentities.IsHuman(player.Identity) || player.Name == null || player.Name.Length > 64 || !players.Add(player.Identity)) throw new InvalidOperationException("Invalid saved player.");
        foreach (var faction in Factions)
        {
            if (faction == null || !Guid.TryParseExact(faction.Id, "N", out _) || !ids.Add(faction.Id) || faction.Name == null || faction.Name.Length < 3 || faction.Name.Length > 48 || faction.Tag == null || faction.Tag.Length < 2 || faction.Tag.Length > 5 || !faction.Tag.All(char.IsAsciiLetterOrDigit) || !tags.Add(faction.Tag) || faction.Members == null || faction.Requests == null || faction.Invitations == null || !faction.Members.Contains(faction.Leader) || faction.Members.Count > 10000 || faction.Requests.Count > 10000 || faction.Invitations.Count > 10000) throw new InvalidOperationException("Invalid saved faction.");
            foreach (long member in faction.Members) if (!players.Contains(member) || !members.Add(member)) throw new InvalidOperationException("Invalid faction membership.");
            if (faction.Requests.Any(p => !players.Contains(p)) || faction.Invitations.Any(p => !players.Contains(p))) throw new InvalidOperationException("Invalid faction invitation or request.");
        }
    }
}

public static class SocialMessage
{
    public const int MaxBytes = 1048576;
    public static byte[] Encode<T>(SyncMsg type, T value)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value);
        if (json.Length >= MaxBytes) throw new ArgumentException("Faction message is too large.");
        byte[] data = new byte[json.Length + 1]; data[0] = (byte)type; json.CopyTo(data, 1); return data;
    }
    public static T Decode<T>(byte[] data)
    {
        if (data == null || data.Length < 3 || data.Length > MaxBytes || (typeof(T) == typeof(SocialRequest) && data.Length > 1024)) throw new ArgumentException("Invalid faction message size.");
        try { return JsonSerializer.Deserialize<T>(data.AsSpan(1)) ?? throw new ArgumentException("Empty faction message."); }
        catch (JsonException e) { throw new ArgumentException("Invalid faction message.", e); }
    }
}
