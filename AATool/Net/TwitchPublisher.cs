using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Windows.Forms;
using System.Xml;
using AATool.Configuration;
using AATool.Data.Categories;
using AATool.Data.Objectives;
using AATool.Data.Progress;
using Newtonsoft.Json;

namespace AATool.Net
{
    // Sends tracker state to the AATool Twitch extension's backend (EBS), which relays it to viewers.
    // The message format and "catalog order" are documented at the top of extension/public/overlay.js.
    public static class TwitchPublisher
    {
        private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1); // Twitch relays at most 1 message/s
        private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(60);    // lets a restarted EBS recover
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);
        private static readonly HttpClient Http = new () { Timeout = TimeSpan.FromSeconds(10) };

        private static List<(string id, List<string> criteria)> catalog;
        private static string catalogVersion;
        private static string lastContent;
        private static DateTime nextCheck, lastSent;
        private static volatile bool sending;

        public static string Status { get; private set; } = "Not connected";

        public static void Update(Time time)
        {
            if (sending || DateTime.UtcNow < nextCheck)
                return;
            nextCheck = DateTime.UtcNow + CheckInterval;

            string server = (Config.Net.TwitchServer.Value ?? string.Empty).Trim().TrimEnd('/');
            string key = (Config.Net.TwitchKey.Value ?? string.Empty).Trim();
            if (server.Length is 0 || key.Length is 0)
            {
                Status = "Not connected";
                return;
            }

            Dictionary<string, object> message;
            bool hidden = Config.Net.TwitchHidden;
            if (hidden)
            {
                //the streamer hid the overlay (e.g. playing another game): no tracker data needed,
                //so this goes out whatever AATool is tracking, or if it isn't tracking anything
                message = new Dictionary<string, object> { { "v", 1 }, { "hide", true } };
            }
            else
            {
                if (Tracker.Category.GetType() != typeof(AllAdvancements))
                {
                    Status = "Only All Advancements is supported";
                    return;
                }
                if (!Tracker.IsWorking)
                {
                    //keep showing viewers the last good state instead of an empty tracker
                    Status = $"Paused: {Tracker.GetStatusText()}";
                    return;
                }
                try
                {
                    message = BuildMessage();
                }
                catch (Exception e)
                {
                    //never let the stream integration take down the tracker
                    Status = $"Couldn't read tracker state: {e.Message}";
                    return;
                }
            }
            string content = JsonConvert.SerializeObject(message);
            if (content == lastContent && DateTime.UtcNow - lastSent < Heartbeat)
                return;

            //age isn't part of the change check; viewers count it up on their own
            if (!hidden)
                message["age"] = (int)Math.Max(time.TotalSeconds - Tracker.LastRefreshSeconds, 0);
            lastContent = content;
            lastSent = DateTime.UtcNow;
            Send($"{server}/publish", key, JsonConvert.SerializeObject(message), hidden);
        }

        // Hide or show the overlay for every viewer; sent right away on the next update.
        public static void SetHidden(bool hidden)
        {
            Config.Net.TwitchHidden.Set(hidden);
            Config.Net.TrySave();
            lastContent = null;
            nextCheck = DateTime.UtcNow;
        }

        // Advancement files sorted by name, then document order -- matches extension/dev.mjs.
        private static void LoadCatalog()
        {
            catalog = new ();
            IEnumerable<string> files = Directory.GetFiles(Paths.System.AdvancementsFolder, "*.xml")
                .OrderBy(file => Path.GetFileName(file), StringComparer.Ordinal);
            foreach (string file in files)
            {
                var document = new XmlDocument();
                document.Load(file);
                foreach (XmlNode advancement in document.SelectNodes("//advancement"))
                {
                    List<string> criteria = advancement.SelectNodes(".//criterion").Cast<XmlNode>()
                        .Select(criterion => criterion.Attributes["id"].Value).ToList();
                    catalog.Add((advancement.Attributes["id"].Value, criteria));
                }
            }
            catalogVersion = Tracker.Category.CurrentVersion;
        }

        private static Dictionary<string, object> BuildMessage()
        {
            if (catalog is null || catalogVersion != Tracker.Category.CurrentVersion)
                LoadCatalog();

            var advancements = new List<bool>();
            var criteria = new List<bool>();
            foreach ((string id, List<string> criteriaIds) in catalog)
            {
                Tracker.TryGetAdvancement(id, out Advancement advancement);
                advancements.Add(advancement?.IsComplete() is true);
                foreach (string criterionId in criteriaIds)
                {
                    Criterion criterion = null;
                    advancement?.Criteria.All.TryGetValue(criterionId, out criterion);
                    criteria.Add(criterion?.IsComplete() is true);
                }
            }

            //every pinnable item for this version, so the streamer can choose which to show in the extension
            var pickups = new Dictionary<string, string[]>();
            foreach (string name in PinnedObjectiveSet.GetAllAvailable())
            {
                if (Tracker.TryGetComplexObjective(name, out ComplexObjective pickup))
                    pickups[name] = new[] { pickup.Icon, pickup.FullStatus?.Replace('\0', ' ') };
            }
            Config.Overlay.PinnedObjectiveList.Value.TryGetCurrentList(out List<string> pinned);

            var message = new Dictionary<string, object> {
                { "v", 1 },
                { "ver", Tracker.Category.CurrentVersion },
                { "cat", Tracker.Category.Name },
                { "n", catalog.Count },
                { "m", criteria.Count },
                { "a", ToBase64Bits(advancements) },
                { "c", ToBase64Bits(criteria) },
                { "igt", (int)Tracker.InGameTime.TotalSeconds },
                { "p", pickups },
                { "pin", pinned ?? new List<string>() },
            };
            if (Tracker.Category.IsComplete())
                message["end"] = BuildCompletion();
            return message;
        }

        // The run stats AATool's own completion screen shows (UIRunComplete.Show).
        private static Dictionary<string, object> BuildCompletion()
        {
            WorldState state = Tracker.State;
            string player = null;
            if (!Peer.IsRunning)
                Player.TryGetName(Tracker.GetMainPlayer(), out player);

            return new Dictionary<string, object> {
                { "player", player },
                { "tool", Main.ShortTitle },
                { "flown", (int)state.KilometersFlown },
                { "bread", state.TimesUsed("minecraft:bread") },
                { "enchants", state.ItemsEnchanted },
                { "pearls", state.TimesUsed("minecraft:ender_pearl") },
                { "temples", state.TimesMined("minecraft:tnt") / 9 },
                { "creepers", state.TimesKilled("minecraft:creeper") },
                { "drowned", state.TimesKilled("minecraft:drowned") },
                { "withers", state.TimesKilled("minecraft:wither_skeleton") },
                { "fish", state.TimesKilled("minecraft:cod") + state.TimesKilled("minecraft:salmon") },
                { "phantoms", state.TimesKilled("minecraft:phantom") },
                { "netherrack", state.TimesMined("minecraft:netherrack") },
                { "debris", state.TimesMined("minecraft:ancient_debris") },
                { "ender_chests", state.TimesMined("minecraft:ender_chest") },
                { "lecterns", state.TimesMined("minecraft:lectern") },
                { "sugarcane", state.TimesPickedUp("minecraft:sugar_cane") },
            };
        }

        private static string ToBase64Bits(List<bool> flags)
        {
            byte[] bytes = new byte[(flags.Count + 7) / 8];
            for (int i = 0; i < flags.Count; i++)
            {
                if (flags[i])
                    bytes[i >> 3] |= (byte)(1 << (i & 7));
            }
            return Convert.ToBase64String(bytes);
        }

        private static async void Send(string url, string key, string body, bool hidden)
        {
            sending = true;
            bool sent = false;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url) {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using HttpResponseMessage response = await Http.SendAsync(request);
                sent = response.IsSuccessStatusCode;
                Status = sent ? (hidden ? $"Overlay hidden for viewers (confirmed {DateTime.Now:T})" : $"Connected, last update {DateTime.Now:T}")
                    : response.StatusCode is HttpStatusCode.Unauthorized ? "Key not recognized, generate a new one in the extension's settings"
                    : $"Server error {(int)response.StatusCode}";
            }
            catch (Exception e)
            {
                Status = $"Can't reach server: {e.GetBaseException().Message}";
            }
            finally
            {
                if (!sent)
                {
                    //resend after a delay
                    lastContent = null;
                    nextCheck = DateTime.UtcNow + RetryDelay;
                }
                sending = false;
            }
        }

        // "Twitch" tab for the settings window, built here so AATool's designer files stay untouched.
        public static TabPage CreateSettingsTab()
        {
            var page = new TabPage("Twitch") { Padding = new Padding(3), UseVisualStyleBackColor = true };
            var intro = new Label {
                Text = "Shows this tracker on your stream through the AATool Twitch extension. Copy the server and key " +
                    "from the extension's settings page (Twitch dashboard > Extensions > AATool > Settings).",
                Location = new System.Drawing.Point(12, 12),
                Size = new System.Drawing.Size(510, 32),
            };
            var server = new TextBox { Location = new System.Drawing.Point(70, 54), Width = 450, Text = Config.Net.TwitchServer.Value ?? string.Empty };
            var key = new TextBox { Location = new System.Drawing.Point(70, 82), Width = 450, Text = Config.Net.TwitchKey.Value ?? string.Empty, UseSystemPasswordChar = true };
            var status = new Label { Location = new System.Drawing.Point(12, 116), Size = new System.Drawing.Size(510, 20) };

            void Save(object sender, EventArgs e)
            {
                Config.Net.TwitchServer.Set(server.Text.Trim());
                Config.Net.TwitchKey.Set(key.Text.Trim());
                Config.Net.TrySave();
                lastContent = null;
                nextCheck = DateTime.UtcNow;
            }
            server.TextChanged += Save;
            key.TextChanged += Save;

            //for switching games mid-stream: takes the overlay (and the on-stream count) off every viewer's screen
            var visibility = new Label {
                Text = "Playing something else? Hide the overlay for all viewers, then show it again when you're back.",
                Location = new System.Drawing.Point(12, 150),
                Size = new System.Drawing.Size(510, 18),
            };
            var hide = new Button { Text = "Hide overlay for everyone", Location = new System.Drawing.Point(12, 172), Size = new System.Drawing.Size(200, 26) };
            var show = new Button { Text = "Show overlay for everyone", Location = new System.Drawing.Point(220, 172), Size = new System.Drawing.Size(200, 26) };
            void UpdateButtons()
            {
                hide.Enabled = !Config.Net.TwitchHidden;
                show.Enabled = Config.Net.TwitchHidden;
            }
            hide.Click += (s, e) => { SetHidden(true); UpdateButtons(); };
            show.Click += (s, e) => { SetHidden(false); UpdateButtons(); };
            UpdateButtons();

            var refresh = new System.Windows.Forms.Timer { Interval = 500, Enabled = true };
            refresh.Tick += (s, e) => status.Text = $"Status: {Status}";
            page.Disposed += (s, e) => refresh.Dispose();

            page.Controls.AddRange(new Control[] {
                intro,
                new Label { Text = "Server", Location = new System.Drawing.Point(12, 57), AutoSize = true },
                server,
                new Label { Text = "Key", Location = new System.Drawing.Point(12, 85), AutoSize = true },
                key,
                status,
                visibility,
                hide,
                show,
            });
            return page;
        }
    }
}
