using System;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using WpfControls = System.Windows.Controls;
using SW = System.Windows;
using System.Diagnostics; // <-- aggiunto per aprire il browser

namespace DeepLocal
{
    public partial class MainWindow : Window
    {
        private const string OllamaHost = "http://127.0.0.1:11434";
        private string _model = "gemma3:12b";
        private static readonly HttpClient http = new();

        private readonly string[] _supported = new[]
        {
            "Italian","English","Spanish","French","German","Russian","Hebrew","Japanese","Chinese"
        };

        private string _lastExplicitSourceLang = "English";
        private bool _isSwapping = false;

        // Hotkey ALT+T
        private const int HOTKEY_ID = 9000;
        private const uint MOD_ALT = 0x0001;
        private const int WM_HOTKEY = 0x0312;
        private const int WM_DPICHANGED = 0x02E0;

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private HwndSource? _source;

        public MainWindow()
        {
            InitializeComponent();

            // Quando torna visibile (es. dalla X → tray → riapri), ri-snap in basso-destra
            this.IsVisibleChanged += (_, e) =>
            {
                if (this.IsVisible)
                {
                    App.PlaceWindowBottomRight(this);
                    App.ResnapAfterFirstLayout(this);
                }
            };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Hotkey ALT+T
            var helper = new WindowInteropHelper(this);
            _source = HwndSource.FromHwnd(helper.Handle);
            _source?.AddHook(HwndHook);
            RegisterHotKey(helper.Handle, HOTKEY_ID, MOD_ALT, (uint)KeyInterop.VirtualKeyFromKey(Key.T));
        }

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);

            // Modello dal combo
            var selItem = ModelCombo?.SelectedItem as WpfControls.ComboBoxItem;
            var tag = selItem?.Tag as string;
            if (!string.IsNullOrWhiteSpace(tag)) _model = tag!;

            // Init last-explicit se non Auto
            var src = GetLang(SourceLang);
            if (!string.Equals(src, "Auto", StringComparison.OrdinalIgnoreCase))
                _lastExplicitSourceLang = src;

            ApplyFlow(SourceLang, SourceBox);
            ApplyFlow(TargetLang, TargetBox);

            StatusModel.Text = $"Model: {_model}  |  Ollama: {OllamaHost}";
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_source != null)
            {
                _source.RemoveHook(HwndHook);
                UnregisterHotKey(_source.Handle, HOTKEY_ID);
            }
            base.OnClosed(e);
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                _ = TranslateClipboardAsync();
                handled = true;
            }
            else if (msg == WM_DPICHANGED)
            {
                // Cambio monitor / scaling: ri-posiziona subito
                App.PlaceWindowBottomRight(this);
            }
            return IntPtr.Zero;
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (WindowState == WindowState.Minimized)
            {
                this.Hide();
                this.ShowInTaskbar = false;
            }
            else
            {
                this.ShowInTaskbar = true;
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);
            if (!DeepLocal.App.AllowClose)
            {
                // Con la X non chiudere: manda in tray
                e.Cancel = true;
                this.WindowState = WindowState.Minimized;
            }
        }

        // ===== Helpers lingue & flow =====
        private static string GetLang(WpfControls.ComboBox cb)
            => (cb.SelectedItem as WpfControls.ComboBoxItem)?.Content?.ToString()?.Trim() ?? "";

        private static void SetLang(WpfControls.ComboBox cb, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            for (int i = 0; i < cb.Items.Count; i++)
            {
                var itemName = (cb.Items[i] as WpfControls.ComboBoxItem)?.Content?.ToString()?.Trim();
                if (string.Equals(itemName, name, StringComparison.OrdinalIgnoreCase))
                {
                    cb.SelectedIndex = i;
                    return;
                }
            }
        }

        private static bool IsRtlLanguage(string lang)
            => string.Equals(lang, "Hebrew", StringComparison.OrdinalIgnoreCase);

        private static void ApplyFlow(WpfControls.ComboBox? langCombo, SW.Controls.TextBox? targetBox)
        {
            if (langCombo == null || targetBox == null) return;
            var lang = (langCombo.SelectedItem as WpfControls.ComboBoxItem)?.Content?.ToString() ?? "";
            targetBox.FlowDirection = IsRtlLanguage(lang)
                ? SW.FlowDirection.RightToLeft
                : SW.FlowDirection.LeftToRight;
        }

        private bool IsSupported(string lang)
        {
            foreach (var s in _supported)
                if (string.Equals(s, lang, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string NormalizeLang(string raw)
        {
            var t = (raw ?? "").Trim();
            if (t.Equals("Chinese (Simplified)", StringComparison.OrdinalIgnoreCase)) return "Chinese";
            if (t.Equals("Chinese (Traditional)", StringComparison.OrdinalIgnoreCase)) return "Chinese";
            if (t.Equals("Hebrew (Hebrew)", StringComparison.OrdinalIgnoreCase)) return "Hebrew";
            return t;
        }

        private void SourceLang_SelectionChanged(object sender, WpfControls.SelectionChangedEventArgs e)
        {
            if (_isSwapping) return;
            if (!IsLoaded || SourceBox == null) return;

            var current = GetLang(SourceLang);
            if (!string.Equals(current, "Auto", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(current))
                _lastExplicitSourceLang = current;

            ApplyFlow(SourceLang, SourceBox);
        }

        private void TargetLang_SelectionChanged(object sender, WpfControls.SelectionChangedEventArgs e)
        {
            if (!IsLoaded || TargetBox == null) return;
            ApplyFlow(TargetLang, TargetBox);
        }

        // ===== Swap =====
        private void Swap_Click(object sender, RoutedEventArgs e)
        {
            string src = GetLang(SourceLang);   // può essere "Auto"
            string dst = GetLang(TargetLang);   // esplicito

            string newSource = dst;
            string newTarget = !string.Equals(src, "Auto", StringComparison.OrdinalIgnoreCase)
                               ? src
                               : _lastExplicitSourceLang;

            _isSwapping = true;
            try
            {
                SetLang(SourceLang, newSource);
                SetLang(TargetLang, newTarget);

                ApplyFlow(SourceLang, SourceBox);
                ApplyFlow(TargetLang, TargetBox);

                if (!string.Equals(newSource, "Auto", StringComparison.OrdinalIgnoreCase))
                    _lastExplicitSourceLang = newSource;
            }
            finally { _isSwapping = false; }

            // Scambia i testi
            var txt = SourceBox?.Text ?? string.Empty;
            SourceBox!.Text = TargetBox?.Text ?? string.Empty;
            TargetBox!.Text = txt;

            StatusText.Text = $"Swapped → Source: {newSource} | Target: {newTarget}";
        }

        // ===== Azioni UI =====
        private async void TranslateBtn_Click(object sender, RoutedEventArgs e)
            => await TranslateAsync(SourceBox?.Text ?? string.Empty);

        private async void ClipboardBtn_Click(object sender, RoutedEventArgs e)
            => await TranslateClipboardAsync();

        private void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            SourceBox?.Clear();
            TargetBox?.Clear();
            StatusText.Text = "Cleared.";
        }

        private void CopyTranslated_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var text = TargetBox?.Text ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    SW.Clipboard.SetText(text);
                    StatusText.Text = "Translation copied to clipboard.";
                }
            }
            catch (Exception ex) { StatusText.Text = "Copy failed: " + ex.Message; }
        }

        public async Task TranslateClipboardAsync()
        {
            try
            {
                if (SW.Clipboard.ContainsText())
                {
                    var txt = SW.Clipboard.GetText();
                    if (!string.IsNullOrWhiteSpace(txt))
                    {
                        SourceBox!.Text = txt;

                        // Mostra e snappa in basso-destra, sempre.
                        App.PlaceWindowBottomRight(this);
                        this.Show();
                        this.Activate();
                        App.ResnapAfterFirstLayout(this);

                        await TranslateAsync(txt);
                    }
                }
            }
            catch (Exception ex) { StatusText.Text = "Clipboard error: " + ex.Message; }
        }

        // ===== Translate con auto-detect =====
        private async Task TranslateAsync(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return;

            // Auto-detect se Source è Auto
            string currentSource = GetLang(SourceLang);
            if (string.Equals(currentSource, "Auto", StringComparison.OrdinalIgnoreCase))
            {
                string detected = await DetectLanguageAsync(input);
                if (!IsSupported(detected))
                {
                    TargetBox.Text = "Unsupported language.";
                    StatusText.Text = "Unsupported source language.";
                    return;
                }

                _isSwapping = true;
                SetLang(SourceLang, detected);
                _isSwapping = false;
                _lastExplicitSourceLang = detected;
                ApplyFlow(SourceLang, SourceBox);
                StatusText.Text = $"Detected language: {detected}";
            }

            string target = GetLang(TargetLang);
            StatusText.Text = $"Translating → {target}...";
            TranslateBtn.IsEnabled = false;
            TargetBox?.Clear();

            try
            {
                var prompt = BuildPrompt(input, target);
                var payload = new { model = _model, prompt, stream = false };
                using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                using var resp = await http.PostAsync($"{OllamaHost}/api/generate", content);
                resp.EnsureSuccessStatusCode();

                using var s = await resp.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(s);
                string raw = doc.RootElement.TryGetProperty("response", out var r) ? r.GetString() ?? "" : "";
                string cleaned = CleanOutput(raw);
                if (TargetBox != null) TargetBox.Text = cleaned;
                StatusText.Text = "Ready.";
            }
            catch (Exception ex) { StatusText.Text = "Error: " + ex.Message; }
            finally { TranslateBtn.IsEnabled = true; }
        }

        private async Task<string> DetectLanguageAsync(string text)
        {
            // Heuristics veloci (script)
            foreach (var ch in text)
            {
                if (ch >= 0x0590 && ch <= 0x05FF) return "Hebrew";
                if (ch >= 0x0400 && ch <= 0x04FF) return "Russian";
                if ((ch >= 0x3040 && ch <= 0x30FF) || (ch >= 0x31F0 && ch <= 0x31FF)) return "Japanese";
                if (ch >= 0x4E00 && ch <= 0x9FFF) return "Chinese";
            }

            string snippet = text.Length > 500 ? text.Substring(0, 500) : text;
            var detectPrompt =
$@"You are a language identifier.
From the following text, answer with ONE label only from this set:
Italian, English, Spanish, French, German, Russian, Hebrew, Japanese, Chinese, Unsupported.

Text:
""{snippet.Replace("\"", "''")}"" 

Answer with exactly one label from the set above.";

            try
            {
                var payload = new { model = _model, prompt = detectPrompt, stream = false };
                using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                using var resp = await http.PostAsync($"{OllamaHost}/api/generate", content);
                resp.EnsureSuccessStatusCode();

                using var s = await resp.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(s);
                var ans = (doc.RootElement.TryGetProperty("response", out var r) ? r.GetString() ?? "" : "").Trim();
                ans = NormalizeLang(ans);

                if (ans.Contains('\n')) ans = ans.Split('\n')[0].Trim();
                if (ans.Contains(' ')) ans = ans.Split(' ')[0].Trim();

                return IsSupported(ans) ? ans : "Unsupported";
            }
            catch
            {
                return "Unsupported";
            }
        }

        private static string BuildPrompt(string input, string targetLang)
        {
            return $@"
You are a professional translator.
Translate the following text into {targetLang}.
Output ONLY the translation in {targetLang} (no explanations, no labels, no quotes).
Preserve line breaks and basic punctuation.

Text:
{input}";
        }

        private static string CleanOutput(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            string t = text.Trim();

            // rimuovi backtick/quote ai bordi
            t = t.Trim().Trim('`').Trim().Trim('"').Trim();

            // rimuovi prefissi comuni
            if (t.StartsWith("Translation:", StringComparison.OrdinalIgnoreCase))
                t = t["Translation:".Length..].TrimStart();
            if (t.StartsWith("Output:", StringComparison.OrdinalIgnoreCase))
                t = t["Output:".Length..].TrimStart();

            // taglia "END" finali eventuali
            var lines = t.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            int end = lines.Length - 1;
            while (end >= 0)
            {
                var l = lines[end].Trim();
                if (l.Equals("END", StringComparison.OrdinalIgnoreCase) ||
                    l.Equals("END.", StringComparison.OrdinalIgnoreCase))
                {
                    end--;
                    continue;
                }
                break;
            }
            return string.Join("\n", lines, 0, end + 1).TrimEnd();
        }

        private void ModelCombo_SelectionChanged(object sender, WpfControls.SelectionChangedEventArgs e)
        {
            var item = ModelCombo?.SelectedItem as WpfControls.ComboBoxItem;
            var newModel = (item?.Tag as string) ?? item?.Content?.ToString();
            if (!string.IsNullOrWhiteSpace(newModel))
                _model = newModel!;

            if (!this.IsLoaded) return;
            StatusModel.Text = $"Model: {_model}  |  Ollama: {OllamaHost}";
        }

        // ===== NUOVO: handler per il pulsante ℹ️ =====
        private void InfoBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://github.com/ShinRalexis") { UseShellExecute = true });
                StatusText.Text = "Opening GitHub…";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Open link failed: " + ex.Message;
            }
        }
    }
}
