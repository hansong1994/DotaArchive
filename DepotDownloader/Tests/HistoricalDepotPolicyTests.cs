using System;
using System.Collections.Generic;
using DepotDownloader;

internal static class HistoricalDepotPolicyTests
{
    private static int checks;
    private static List<(uint depotId, ulong manifestId)> Valid()
        => new() { (571u, 100ul), (572u, 101ul), (573u, 102ul), (575u, 103ul) };

    private static void Pass(Action action)
    {
        action();
        checks++;
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (ArgumentException) { checks++; return; }
        throw new Exception("Historical mode accepted an invalid request.");
    }

    private static int Main()
    {
        Pass(() => HistoricalDepotPolicy.Validate(570, Valid(), "public", false, null));
        Pass(() => HistoricalDepotPolicy.Validate(570, Valid(), "PUBLIC", false, ""));
        Reject(() => HistoricalDepotPolicy.Validate(730, Valid(), "public", false, null));
        Reject(() => HistoricalDepotPolicy.Validate(570, Valid(), "beta", false, null));
        Reject(() => HistoricalDepotPolicy.Validate(570, Valid(), null, false, null));
        Reject(() => HistoricalDepotPolicy.Validate(570, Valid(), "public", true, null));
        Reject(() => HistoricalDepotPolicy.Validate(570, Valid(), "public", false, "password"));
        Reject(() => HistoricalDepotPolicy.Validate(570, null, "public", false, null));
        Reject(() => HistoricalDepotPolicy.Validate(570, new(), "public", false, null));
        Reject(() => HistoricalDepotPolicy.Validate(570, new() { (0u, 1ul) }, "public", false, null));
        Reject(() => HistoricalDepotPolicy.Validate(570, new() { (uint.MaxValue, 1ul) }, "public", false, null));
        Reject(() => HistoricalDepotPolicy.Validate(570, new() { (571u, 0ul) }, "public", false, null));
        Reject(() => HistoricalDepotPolicy.Validate(570, new() { (571u, ulong.MaxValue) }, "public", false, null));
        Reject(() => HistoricalDepotPolicy.Validate(570, new() { (571u, 1ul), (571u, 2ul) }, "public", false, null));
        Pass(() => HistoricalDepotPolicy.ValidateProxySource(570, uint.MaxValue));
        Pass(() => HistoricalDepotPolicy.ValidateProxySource(570, 570));
        Reject(() => HistoricalDepotPolicy.ValidateProxySource(570, 730));
        Reject(() => HistoricalDepotPolicy.ValidateProxySource(570, 0));
        Console.WriteLine("HistoricalDepotPolicy: " + checks + " offline checks passed.");
        return 0;
    }
}
