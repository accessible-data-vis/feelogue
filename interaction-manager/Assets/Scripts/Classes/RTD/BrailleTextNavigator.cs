using UnityEngine;

/// <summary>What the braille line is currently showing, for text navigation.</summary>
public enum BrailleLineOwner { None, Answer, Presentation }

/// <summary>
/// Moves through the text on the braille line. Next/Prev (pan tap) page the line and
/// carry on into the next answer chunk or presentation layer. Skip (pan + F1) jumps a
/// whole chunk or layer.
/// </summary>
public class BrailleTextNavigator
{
    private readonly InterfaceRTDUpdater _rtd;
    private readonly AgentResponseHandler _answers;

    public BrailleTextNavigator(InterfaceRTDUpdater rtd, AgentResponseHandler answers)
    {
        _rtd = rtd;
        _answers = answers;
    }

    public void Next()
    {
        if (_rtd.TryNextBraillePage()) return;
        switch (_rtd.LineOwner)
        {
            case BrailleLineOwner.Answer: StepAnswer(+1); break;
            case BrailleLineOwner.Presentation: StepPresentation(+1); break;
            default: AppLog.Detail(LogArea.Buttons, "End of the braille text"); break;
        }
    }

    public void Prev()
    {
        if (_rtd.TryPrevBraillePage()) return;
        switch (_rtd.LineOwner)
        {
            case BrailleLineOwner.Answer: StepAnswer(-1); break;
            case BrailleLineOwner.Presentation: StepPresentation(-1); break;
            default: AppLog.Detail(LogArea.Buttons, "Start of the braille text"); break;
        }
    }

    public void Skip(int dir)
    {
        switch (_rtd.LastTextSequence)
        {
            case BrailleLineOwner.Answer: StepAnswer(dir); break;
            case BrailleLineOwner.Presentation: StepPresentation(dir); break;
            default: AppLog.Detail(LogArea.Buttons, "Nothing to skip through"); break;
        }
    }

    private void StepAnswer(int dir)
    {
        bool moved = dir > 0 ? _answers.AdvanceToNextChunk() : _answers.StepBackInChunk();
        // Past the last chunk of an answer given during the presentation, carry on
        // with the next layer.
        if (!moved && dir > 0 && _rtd.IsPresentationActive)
            StepPresentation(+1);
    }

    private void StepPresentation(int dir)
    {
        if (!_rtd.IsPresentationActive) return;
        if (dir > 0)
        {
            // Past the summary layer the presentation ends and the filter returns.
            if (!_rtd.NextOverviewLayer())
                _rtd.EndPresentation(announce: true);
        }
        else
        {
            _rtd.PrevOverviewLayer();
        }
    }
}
