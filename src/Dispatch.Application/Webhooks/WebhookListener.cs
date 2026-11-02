using Dispatch.Domain;

namespace Dispatch.Application.Webhooks;

/// <summary>
/// The webhook receiver as a message-check listener. A <see cref="MessageExpectation"/> whose <see cref="MessageExpectation.ListenerId"/>
/// is <see cref="Id"/> waits for an HTTP callback instead of a broker or stream message; its <see cref="MessageExpectation.Channel"/>
/// is the path the callback must arrive at (e.g. <c>/hooks/orders</c>, <c>POST /hooks/*</c>), blank for any path.
/// </summary>
public static class WebhookListener
{
    /// <summary>A fixed id, so saved collections and exports refer to the receiver without a saved request.</summary>
    public static readonly Guid Id = Guid.Parse("7e0b0d8c-0f3a-4c6e-9a1d-5c2b7f4e8a90");

    public const string Name = "Webhook receiver";

    public static bool Is(MessageExpectation expectation) => expectation.ListenerId == Id;

    public static bool Is(Guid listenerId) => listenerId == Id;
}
