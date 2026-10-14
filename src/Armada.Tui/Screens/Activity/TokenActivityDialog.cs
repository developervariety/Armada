namespace Armada.Tui.Screens.Activity
{
    using System;
    using Armada.Core.Models;
    using Armada.Tui.Modals;
    using Armada.Tui.Services;
    using Armada.Tui.Text;
    using Armada.Tui.Widgets;
    using TUIKit;
    using TUIKit.Input;

    /// <summary>Full calendar inspector on short terminals; arrows select days and t changes token type.</summary>
    public class TokenActivityDialog : ArmadaDialog
    {
        private readonly FocusScope _Scope = new FocusScope();
        private readonly TokenUsageSummaryResult? _Data;
        private readonly DateTime _From;
        private readonly DateTime _To;
        private int _Metric = 0;
        private readonly string[] _Metrics = new string[] { "total", "input", "output", "cached" };

        /// <summary>The focusable daily token calendar.</summary>
        public TokenActivityHeatmap Heatmap { get; } = new TokenActivityHeatmap();

        /// <summary>Instantiate an inspector from a daily summary.</summary>
        /// <param name="context">UI context.</param>
        /// <param name="data">Daily summary.</param>
        /// <param name="from">First UTC date.</param>
        /// <param name="to">Last UTC date.</param>
        public TokenActivityDialog(TuiContext context, TokenUsageSummaryResult? data, DateTime from, DateTime to)
            : base("Token activity", context.Loc, context.Theme.Current)
        {
            _Data = data; _From = from; _To = to;
            Heatmap.Localizer = Localizer;
            Heatmap.ApplyTheme(Theme);
            _Scope.Add(Heatmap);
            _Scope.Focus(Heatmap);
            _Scope.SetActive(true);
            Heatmap.SetUsage(data, from, to);
            Heatmap.Select(to);
            FooterHint = " t " + T("Metric") + "  " + T("Arrow keys") + "  Esc " + T("Close") + " ";
            MaxContentWidth = 160;
        }

        /// <inheritdoc />
        public override bool HandleKey(KeyEvent key)
        {
            if (key.Code == KeyCode.Escape) { RequestClose(null); return true; }
            if (key.Code == KeyCode.Character && Char.ToLowerInvariant((char)key.Rune) == 't')
            {
                _Metric = (_Metric + 1) % _Metrics.Length;
                Heatmap.SetUsage(_Data, _From, _To, _Metrics[_Metric]);
                return true;
            }
            return _Scope.HandleKey(key);
        }

        /// <inheritdoc />
        protected override int MeasureContentWidth(int availableWidth) { return Math.Min(availableWidth, 140); }

        /// <inheritdoc />
        protected override int MeasureContentHeight(int contentWidth) { return 13; }

        /// <inheritdoc />
        protected override void RenderContent(ISurface content)
        {
            SurfaceText.Draw(content, 0, 0, T("Daily token totals (UTC)") + " / " + T(_Metrics[_Metric] == "total" ? "Total" : _Metrics[_Metric] == "input" ? "Input" : _Metrics[_Metric] == "output" ? "Output" : "Cached"), Dim(), content.Size.Width);
            _Scope.RenderChild(content, Heatmap, new Rect(0, 1, content.Size.Width, Math.Max(0, content.Size.Height - 1)));
        }
    }
}
