using Fworker.Contracts.Seams;

// Seam S3 (task W1.6, docs/design/fworker-wolverine-refactor.md 4): v1 does what the webhook module and VerbPipeline did by themselves before the seam.
// A webhook event is published on the hub (the dashboard stream and the waiters); a sweep request is queued on the hub for the sweeper. Nothing is
// deduplicated or delayed here: the webhook's delivery dedup stays in the module and the sweeper coalesces requests, as before.
sealed class V1EventIngress(EventHub hub) : IEventIngress
{
    public void Ingest(IngressEvent ev)
    {
        switch (ev)
        {
            case WebhookEventIngested w: hub.Publish(w.Json); break;
            case SweepRequested s: hub.RequestSweep(s.Item); break;
            default: throw new ArgumentOutOfRangeException(nameof(ev), ev.GetType().Name, "an event kind the v1 ingress does not know");
        }
    }
}
