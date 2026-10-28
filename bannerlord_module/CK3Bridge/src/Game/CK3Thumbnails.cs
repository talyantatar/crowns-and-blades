using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.MountAndBlade.View.Tableaus;
using TaleWorlds.MountAndBlade.View.Tableaus.Thumbnails;
using TaleWorlds.ObjectSystem;
using Path = System.IO.Path;

namespace CK3Bridge
{
    /// <summary>
    /// The "thumbnails" command (a build step of the CK3 mod's artifact icons, tools/gen/equipment/item_icons.mjs): renders every item
    /// listed in %LOCALAPPDATA%\ck3xbl\thumbs\items.txt (one id per line) with the inventory's own thumbnail renderer and saves it as
    /// thumbs\&lt;id&gt;.png; thumbs\done.txt then says how many were written and which ids failed. A few requests at a time, ticked
    /// from the controller; never during a battle.
    /// </summary>
    internal sealed class CK3Thumbnails
    {
        public const int InFlight = 4;
        public static readonly TimeSpan PerItemTimeout = TimeSpan.FromSeconds(20);

        private readonly string _folder;
        private readonly Queue<string> _todo;
        private readonly Dictionary<string, DateTime> _pending = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly List<string> _failed = new List<string>();
        private int _written;

        public bool Done { get; private set; }

        private CK3Thumbnails(string folder, IEnumerable<string> ids)
        {
            _folder = folder;
            _todo = new Queue<string>(ids);
        }

        /// <summary>The job of thumbs\items.txt, or null when there is none.</summary>
        public static CK3Thumbnails Start()
        {
            string folder = Path.Combine(BridgeIo.Folder, "thumbs");
            string list = Path.Combine(folder, "items.txt");
            if (!File.Exists(list))
            {
                BridgeIo.Log($"thumbnails: {list} not found");
                return null;
            }
            string done = Path.Combine(folder, "done.txt");
            if (File.Exists(done)) File.Delete(done);
            var ids = File.ReadAllLines(list).Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList();
            BridgeIo.Log($"thumbnails: {ids.Count} item(s) to render into {folder}");
            return new CK3Thumbnails(folder, ids);
        }

        public void Tick()
        {
            if (Done) return;
            DateTime now = DateTime.UtcNow;
            foreach (var late in _pending.Where(p => now - p.Value > PerItemTimeout).Select(p => p.Key).ToList())
            {
                _pending.Remove(late);
                _failed.Add(late + " (timed out)");
            }
            while (_pending.Count < InFlight && _todo.Count > 0) Request(_todo.Dequeue());
            if (_todo.Count == 0 && _pending.Count == 0) Finish();
        }

        private void Request(string id)
        {
            ItemObject item = MBObjectManager.Instance.GetObject<ItemObject>(id);
            if (item == null) { _failed.Add(id + " (unknown item)"); return; }
            string path = Path.Combine(_folder, id + ".png");
            _pending[id] = DateTime.UtcNow;
            try
            {
                ThumbnailCacheManager.Current.CreateTexture(new ItemThumbnailCreationData(item, "", texture =>
                {
                    try
                    {
                        texture.SaveToFile(path, false);
                        _written++;
                    }
                    catch (Exception e) { _failed.Add($"{id} ({e.Message})"); }
                    _pending.Remove(id);
                }, () =>
                {
                    _failed.Add(id + " (cancelled)");
                    _pending.Remove(id);
                }));
            }
            catch (Exception e)
            {
                _pending.Remove(id);
                _failed.Add($"{id} ({e.Message})");
            }
        }

        private void Finish()
        {
            Done = true;
            var lines = new List<string> { $"written {_written}", $"failed {_failed.Count}" };
            lines.AddRange(_failed);
            File.WriteAllLines(Path.Combine(_folder, "done.txt"), lines);
            BridgeIo.Log($"thumbnails: {_written} written, {_failed.Count} failed");
        }
    }
}
