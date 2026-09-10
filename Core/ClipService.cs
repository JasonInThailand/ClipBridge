using ClipBridge.Clip;
using ClipBridge.Models;

namespace ClipBridge.Core;

/// <summary>The brain: owns the store, computes slot order, applies local and remote changes, tracks modes.</summary>
public sealed class ClipService
{
    public Settings Settings { get; }
    public Store Store { get; }
    public string Machine => Settings.MachineName;

    /// <summary>List content or order changed (UI should refresh).</summary>
    public event Action? Changed;
    /// <summary>A brand-new local item (carries ImagePng / file data) that peers need in full.</summary>
    public event Action<ClipItem>? LocalItemAdded;
    /// <summary>Metadata-only local change (pin, delete, touch, OCR text).</summary>
    public event Action<ClipItem>? LocalMetaChanged;
    /// <summary>Short user-facing notice (tray balloon).</summary>
    public event Action<string>? Notify;
    /// <summary>A new image item needs OCR.</summary>
    public event Action<ClipItem>? OcrWanted;

    public bool TypeMode { get; private set; }
    public bool QueueActive { get; private set; }
    private readonly List<string> _queue = new();
    private int _queueIndex;
    public int QueueRemaining => Math.Max(0, _queue.Count - _queueIndex);

    private readonly object _gate = new();

    public ClipService(Settings settings, Store store)
    {
        Settings = settings;
        Store = store;
    }

    // ---------- Slots ----------

    /// <summary>Pinned items (in pin order) occupy slots 1..k, then unpinned newest-first.</summary>
    public List<ClipItem> Slots()
    {
        var live = Store.Live();
        var pins = live.Where(i => i.Pinned).OrderBy(i => i.PinOrder).ThenBy(i => i.CreatedUtc).Take(Settings.MaxPins);
        var rest = live.Where(i => !i.Pinned).OrderByDescending(i => i.CreatedUtc);
        return pins.Concat(rest).ToList();
    }

    public ClipItem? Slot(int n) => n >= 1 ? Slots().ElementAtOrDefault(n - 1) : null;

    // ---------- Local capture ----------

    public ClipItem? CaptureFromClipboard()
    {
        if (!Settings.CaptureEnabled) return null;
        var item = ClipboardIO.Read(Settings.MaxImageMB * 1024 * 1024, Machine);
        if (item == null) return null;
        return AddLocal(item);
    }

    public ClipItem AddLocal(ClipItem item)
    {
        ClipItem result;
        bool isNew;
        lock (_gate)
        {
            var existing = Store.FindLiveByHash(item.Hash);
            if (existing != null)
            {
                if (!existing.Pinned)
                {
                    existing.CreatedUtc = ClipItem.NowMs();
                    Bump(existing);
                    Store.Upsert(existing);
                    LocalMetaChanged?.Invoke(existing);
                }
                result = existing; isNew = false;
            }
            else
            {
                Store.Upsert(item);
                Trim();
                result = item; isNew = true;
            }
            QueueAdd(result.Id);
        }
        Changed?.Invoke();
        if (isNew)
        {
            LocalItemAdded?.Invoke(item);
            if (item.Kind == ClipKind.Image && Settings.EnableOcr) OcrWanted?.Invoke(item);
        }
        return result;
    }

    private void Trim()
    {
        var unpinned = Store.Live().Where(i => !i.Pinned).OrderByDescending(i => i.CreatedUtc).Skip(Settings.MaxItems).ToList();
        foreach (var old in unpinned)
        {
            old.Deleted = true; Bump(old); Store.Upsert(old);
            LocalMetaChanged?.Invoke(old);
        }
    }

    private static void Bump(ClipItem it) { it.Version++; it.ModifiedUtc = ClipItem.NowMs(); }

    // ---------- Pins / delete ----------

    public bool Pin(string id)
    {
        lock (_gate)
        {
            var it = Store.Get(id); if (it == null || it.Deleted || it.Pinned) return false;
            var pins = Store.Live().Where(i => i.Pinned).ToList();
            if (pins.Count >= Settings.MaxPins) { Notify?.Invoke($"Already {Settings.MaxPins} pinned. Unpin one first."); return false; }
            it.Pinned = true; it.PinOrder = pins.Count == 0 ? 1 : pins.Max(p => p.PinOrder) + 1;
            Bump(it); Store.Upsert(it); LocalMetaChanged?.Invoke(it);
        }
        Changed?.Invoke();
        return true;
    }

    public void Unpin(string id)
    {
        lock (_gate)
        {
            var it = Store.Get(id); if (it == null || !it.Pinned) return;
            it.Pinned = false; it.PinOrder = 0; Bump(it); Store.Upsert(it); LocalMetaChanged?.Invoke(it);
        }
        Changed?.Invoke();
    }

    public void TogglePin(string id)
    {
        var it = Store.Get(id); if (it == null) return;
        if (it.Pinned) Unpin(id); else Pin(id);
    }

    public void UnpinAll()
    {
        lock (_gate)
        {
            foreach (var it in Store.Live().Where(i => i.Pinned))
            {
                it.Pinned = false; it.PinOrder = 0; Bump(it); Store.Upsert(it); LocalMetaChanged?.Invoke(it);
            }
        }
        Changed?.Invoke();
    }

    public void Delete(string id)
    {
        lock (_gate)
        {
            var it = Store.Get(id); if (it == null || it.Deleted) return;
            it.Deleted = true; it.Pinned = false; it.PinOrder = 0; Bump(it); Store.Upsert(it); LocalMetaChanged?.Invoke(it);
        }
        Changed?.Invoke();
    }

    public void ClearUnpinned()
    {
        lock (_gate)
        {
            foreach (var it in Store.Live().Where(i => !i.Pinned))
            {
                it.Deleted = true; Bump(it); Store.Upsert(it); LocalMetaChanged?.Invoke(it);
            }
        }
        Changed?.Invoke();
    }

    public void SetOcrText(string id, string text)
    {
        lock (_gate)
        {
            var it = Store.Get(id); if (it == null || it.Deleted) return;
            it.Text = text; it.OcrDone = true; Bump(it); Store.Upsert(it); LocalMetaChanged?.Invoke(it);
        }
        Changed?.Invoke();
    }

    // ---------- Remote merge ----------

    private static bool Newer(ClipItem incoming, ClipItem local) =>
        incoming.Version > local.Version || (incoming.Version == local.Version && incoming.ModifiedUtc > local.ModifiedUtc);

    /// <summary>Full item from a peer. Returns true if it was stored.</summary>
    public bool ApplyRemoteItem(ClipItem remote)
    {
        bool stored = false, wasNew = false;
        lock (_gate)
        {
            var local = Store.Get(remote.Id);
            if (local == null || Newer(remote, local))
            {
                if (remote.Kind == ClipKind.Image && remote.Thumb == null && remote.ImagePng != null)
                {
                    var (w, h, thumb) = ClipboardIO.MakeThumb(remote.ImagePng);
                    remote.Thumb = thumb; if (remote.ImageWidth == 0) { remote.ImageWidth = w; remote.ImageHeight = h; }
                }
                Store.Upsert(remote);
                stored = true; wasNew = local == null;

                // Same content copied independently on both machines: keep the newer one only.
                if (!remote.Deleted)
                {
                    var dup = Store.Live().FirstOrDefault(i => i.Id != remote.Id && i.Hash == remote.Hash);
                    if (dup != null)
                    {
                        var loser = dup.CreatedUtc <= remote.CreatedUtc ? dup : Store.Get(remote.Id)!;
                        if (loser.Pinned) loser = loser == dup ? Store.Get(remote.Id)! : dup;
                        if (!loser.Pinned) { loser.Deleted = true; Bump(loser); Store.Upsert(loser); LocalMetaChanged?.Invoke(loser); }
                    }
                }
                if (wasNew && !remote.Deleted) QueueAdd(remote.Id);
                Trim();
            }
        }
        if (stored) Changed?.Invoke();
        return stored;
    }

    /// <summary>Metadata-only update from a peer. Returns false when we do not have the item (caller should request it in full).</summary>
    public bool ApplyRemoteMeta(ClipItem meta)
    {
        bool changed = false;
        lock (_gate)
        {
            var local = Store.Get(meta.Id);
            if (local == null) return meta.Deleted; // nothing to delete; otherwise we need the full item
            if (Newer(meta, local))
            {
                local.Text = string.IsNullOrEmpty(meta.Text) && local.Kind == ClipKind.Image ? local.Text : meta.Text;
                local.CreatedUtc = meta.CreatedUtc; local.ModifiedUtc = meta.ModifiedUtc; local.Version = meta.Version;
                local.Pinned = meta.Pinned; local.PinOrder = meta.PinOrder; local.Deleted = meta.Deleted; local.OcrDone = meta.OcrDone;
                Store.Upsert(local);
                changed = true;
            }
        }
        if (changed) Changed?.Invoke();
        return true;
    }

    // ---------- Modes ----------

    public void ToggleTypeMode()
    {
        TypeMode = !TypeMode;
        Notify?.Invoke(TypeMode ? "Type-it-out mode ON: number hotkeys will type the text as keystrokes." : "Type-it-out mode OFF: back to normal paste.");
        Changed?.Invoke();
    }

    public void ToggleQueue()
    {
        lock (_gate)
        {
            QueueActive = !QueueActive;
            _queue.Clear(); _queueIndex = 0;
        }
        Notify?.Invoke(QueueActive ? "Paste queue ON: copy several things, then Ctrl+Alt+P pastes them one at a time in order." : "Paste queue OFF.");
        Changed?.Invoke();
    }

    private void QueueAdd(string id)
    {
        if (!QueueActive) return;
        _queue.Remove(id);
        _queue.Add(id);
        Log.Write($"queue: added {id[..8]} ({_queue.Count} queued, index {_queueIndex})");
    }

    public ClipItem? QueueNext()
    {
        ClipItem? found = null;
        string? notice = null;
        lock (_gate)
        {
            if (!QueueActive) notice = "Paste queue is off. Press Ctrl+Alt+Q to start one.";
            else
            {
                while (_queueIndex < _queue.Count)
                {
                    var it = Store.Get(_queue[_queueIndex++]);
                    if (it != null && !it.Deleted) { found = it; break; }
                }
                if (found == null) notice = "Paste queue is empty. Copy more, or Ctrl+Alt+Q to stop.";
            }
            Log.Write($"queue: next -> {(found == null ? "none" : found.Id[..8])} ({_queue.Count} queued, index {_queueIndex}, active {QueueActive})");
        }
        if (notice != null) Notify?.Invoke(notice);
        Changed?.Invoke();
        return found;
    }
}
