namespace CodexUsageMonitor.Core;

// OFFLINE ONLY. No sender or HTTP dependencies; these are synthetic transition records.
public sealed class MockDeliveryRecord
{
    public string Key { get; set; } = string.Empty;
    public string Status { get; set; } = "MOCK_PENDING";
    public string? MockOwner { get; set; }
}

public static class MockDeliveryController
{
    public const string WindowsMock = "MOCK_WINDOWS";
    public const string MacMock = "MOCK_MAC";

    private static readonly HashSet<string> AllowedStatus = new(StringComparer.Ordinal)
    {
        "MOCK_PENDING", "MOCK_RESERVED", "MOCK_COMPLETE", "MOCK_UNCERTAIN"
    };

    // The caller must persist the changed state under ExclusiveMockWriter
    // BEFORE showing mock output. A crash after reserve never auto-retries.
    public static string? Reserve(UsageEventState state, string key, string mockOwner)
    {
        Validate(state);
        ValidateOwner(state, mockOwner);
        var entry = state.MockDeliveries.SingleOrDefault(e => e.Key == key)
            ?? throw new InvalidOperationException("Unknown synthetic event.");
        if (entry.Status != "MOCK_PENDING") return null;

        var ev = state.Outbox.SingleOrDefault(e => e.Key == key)
            ?? throw new InvalidOperationException("Missing linked synthetic event.");
        entry.Status = "MOCK_RESERVED";
        entry.MockOwner = mockOwner;
        return MockTelegramFormatter.Format(ev);
    }

    public static void Acknowledge(UsageEventState state, string key, string mockOwner)
    {
        var entry = OwnedReservation(state, key, mockOwner);
        entry.Status = "MOCK_COMPLETE";
    }

    public static void MarkUncertain(UsageEventState state, string key, string mockOwner)
    {
        var entry = OwnedReservation(state, key, mockOwner);
        entry.Status = "MOCK_UNCERTAIN";
    }

    private static MockDeliveryRecord OwnedReservation(UsageEventState state, string key, string owner)
    {
        Validate(state);
        ValidateOwner(state, owner);
        var entry = state.MockDeliveries.SingleOrDefault(e => e.Key == key)
            ?? throw new InvalidOperationException("Unknown synthetic event.");
        if (entry.Status != "MOCK_RESERVED" || entry.MockOwner != owner)
            throw new InvalidOperationException("No owned synthetic reservation.");
        return entry;
    }

    private static void ValidateOwner(UsageEventState state, string owner)
    {
        if (owner != WindowsMock && owner != MacMock)
            throw new InvalidOperationException("Only mock host identities allowed.");
        if (state.MockSenderOwner is not null && state.MockSenderOwner != owner)
            throw new InvalidOperationException("Single mock delivery owner violation.");
        state.MockSenderOwner = owner;
    }

    private static void Validate(UsageEventState state)
    {
        if (state.SchemaVersion != 1 || state.MockDeliveries is null ||
            state.MockDeliveries.Any(e => !AllowedStatus.Contains(e.Status) || string.IsNullOrEmpty(e.Key)))
            throw new InvalidOperationException("Invalid mock outbox state.");
    }
}
