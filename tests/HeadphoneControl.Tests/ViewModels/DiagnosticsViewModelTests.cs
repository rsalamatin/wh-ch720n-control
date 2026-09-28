using HeadphoneControl.Diagnostics;
using HeadphoneControl.ViewModels;

namespace HeadphoneControl.Tests.ViewModels;

public class DiagnosticsViewModelTests
{
    [Test]
    public async Task WhenLineIsAppendedThenTextContainsIt()
    {
        var journal = new DiagnosticsJournal();
        using var viewModel = new DiagnosticsViewModel(journal, action => action());

        journal.Append("RX 3e 0c");

        await Assert.That(viewModel.Text).IsEqualTo("RX 3e 0c");
    }

    [Test]
    public async Task WhenJournalIsFullThenOldestLineIsDropped()
    {
        var journal = new DiagnosticsJournal(capacity: 2);
        using var viewModel = new DiagnosticsViewModel(journal, action => action());

        journal.Append("one");
        journal.Append("two");
        journal.Append("three");

        await Assert.That(viewModel.Text).IsEqualTo($"two{Environment.NewLine}three");
    }

    [Test]
    public async Task WhenLinesArriveBeforeDispatchRunsThenOnlyOneUpdateIsQueued()
    {
        var journal = new DiagnosticsJournal();
        var queued = new List<Action>();
        using var viewModel = new DiagnosticsViewModel(journal, queued.Add);

        journal.Append("one");
        journal.Append("two");

        await Assert.That(queued.Count).IsEqualTo(1);
    }

    [Test]
    public async Task WhenClearCommandExecutedThenTextIsEmpty()
    {
        var journal = new DiagnosticsJournal();
        using var viewModel = new DiagnosticsViewModel(journal, action => action());
        journal.Append("one");

        viewModel.ClearCommand.Execute(null);

        await Assert.That(viewModel.Text).IsEmpty();
    }
}
