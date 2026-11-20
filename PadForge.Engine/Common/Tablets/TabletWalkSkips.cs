namespace PadForge.Engine.Tablets;

/// <summary>
/// The HID collections a tablet walk passes by without opening. One whose
/// own descriptor is no tablet stays passed by while it is present. One
/// that could not be opened or read is passed by until a walk tries the
/// unreadable ones again: a walk after a device or raw input change, and the
/// thirty-second walk (<see cref="WindowsTabletReader.FallbackWalkMs"/>).
/// Such a collection used to owe a walk on every walk, so one that never
/// opened kept the full walk running every two seconds. Read and written
/// by one walk at a time.
/// </summary>
internal sealed class TabletWalkSkips
{
    private readonly HashSet<string> notTablets = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> unreadable = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Starts a walk. <paramref name="retryUnreadable"/> opens the
    /// collections that could not be read again.</summary>
    public void BeginWalk(bool retryUnreadable)
    {
        if (retryUnreadable) unreadable.Clear();
    }

    public bool Skip(string path) => notTablets.Contains(path) || unreadable.Contains(path);

    public void NoteNotATablet(string path) => notTablets.Add(path);

    public void NoteUnreadable(string path) => unreadable.Add(path);

    /// <summary>Forgets every path a walk did not meet, so a collection that
    /// comes back is read again.</summary>
    public void KeepPresent(HashSet<string> present)
    {
        notTablets.IntersectWith(present);
        unreadable.IntersectWith(present);
    }
}
