using System.Text.Json.Nodes;

// What a component needs to know of the leader lease (Leader.cs, issue 94), without the lease itself: implemented by GatewayLeader and
// registered as the same instance, so a later handler can take it from the container (C3).
interface ILeadership
{
    bool On { get; }
    // the sweep, the ticks and the back-merge trigger run only while this is true (always, when the lease is off)
    bool MayAct { get; }
    // a newer process superseded this one: it serves nothing but its status
    bool Replaced { get; }
    long Epoch { get; }
    JsonObject Status();
}
