using Rondel.Core.Flow;
using Rondel.Core.Localization;
using Rondel.Windows.Interop;
using Rondel.Windows.UI;

namespace Rondel.Windows;

/// <summary>
/// Quick Conversation's first answer, on a card of its own: the prompt card closes and gives the focus back, and the
/// answer card floats where it was until it is closed or continues in a terminal, so a slow answer blocks nothing.
/// </summary>
internal sealed partial class AppHost
{
    readonly List<AnswerCardWindow> _answerCards = [];

    void ShowAnswerCard(QuickAnswer answer)
    {
        var spec = _menu.CurrentInput;
        var (card, work) = _menu.CardPlace();
        if (work.Width <= 0)
            (work, _) = Native.GetMonitorWorkArea(Native.GetCursorPosition());
        var header = new AnswerHeader(spec?.Title ?? Loc.Get("Action.Quick"), spec?.Glyph ?? '\uE945', spec?.Location, spec?.Image,
            spec?.AgentName ?? "");
        _menu.CloseMenu(restoreFocus: true);
        var window = new AnswerCardWindow(answer, header, RondelPalette.For(Store.Current.Appearance, SystemTheme.Read()));
        window.Closed += (_, _) => _answerCards.Remove(window);
        _answerCards.Add(window);
        window.ShowAt(card, work, offset: _answerCards.Count - 1);
    }

    void CloseAnswerCards()
    {
        foreach (var window in _answerCards.ToList())
            window.Close();
    }
}
