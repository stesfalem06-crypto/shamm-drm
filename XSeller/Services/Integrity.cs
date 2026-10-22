namespace XSeller.Services;

/// <summary>Tamper checks shown in the admin dashboard and the closing screen.</summary>
public static class Integrity
{
    public static List<string> Warnings(LedgerDatabase ledger, Closings closings)
    {
        var list = new List<string>();
        try
        {
            var st = LedgerChain.Verify(ledger.ConnectionString);
            if (!st.Ok) list.Add("Sales ledger tampered: " + string.Join(" ", st.Problems.Take(5)));
            var hist = closings.History();
            var last = hist.LastOrDefault();
            if (last != null)
            {
                if (st.LastId < last.LastSaleId) list.Add($"Sales up to #{last.LastSaleId} were in closing #{last.Number} but are now missing.");
                // The sale pinned by the last closing must still have the same hash.
                var pinned = ledger.GetSaleHash(last.LastSaleId);
                if (last.LastSaleId > 0 && pinned != null && pinned != last.LastSaleHash)
                    list.Add($"Sales covered by closing #{last.Number} were changed after closing.");
            }
            list.AddRange(closings.VerifyHistory().Select(p => "Closing history tampered: " + p));
            var expectedClosings = Accounts.Instance.Get("closing_count");
            if (int.TryParse(expectedClosings, out var n) && n > hist.Count)
                list.Add($"Closing history shows {hist.Count} closing(s) but {n} were recorded — history was deleted.");
        }
        catch (Exception ex) { list.Add("Integrity check failed: " + ex.Message); }
        var clock = LedgerChain.ClockWarning();
        if (clock != null) list.Add(clock);
        return list;
    }
}
