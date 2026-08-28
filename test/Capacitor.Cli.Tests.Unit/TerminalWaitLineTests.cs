namespace Capacitor.Cli.Tests.Unit;

// The block's row bookkeeping and its cursor discipline. Both are invisible from the outside — an
// erase that gets the count wrong takes a line the caller already committed to the scrollback with it,
// and a hide with no matching show leaves the shell without a cursor after setup has exited.
public class TerminalWaitLineTests {
    const string Hide  = "\u001b[?25l";
    const string Show  = "\u001b[?25h";
    const string Clear = "\u001b[2K\r";
    const string Up    = "\u001b[1A";

    // Width is injected: the wrap it guards against happens inside Spectre's writer, so the only
    // observable consequence is the row count, and the real console here is a test host's.
    static (TerminalWaitLine Line, StringWriter Control) Build(bool tty = true, int? width = 80) {
        var control = new StringWriter();

        return (new TerminalWaitLine(tty, control, () => width), control);
    }

    [Test]
    public async Task An_offer_makes_the_block_two_rows_and_dropping_it_makes_it_one() {
        var (line, _) = Build();

        line.Show("waiting", "t to carry on here");
        await Assert.That(line.Drawn).IsEqualTo(2);

        // The offer is withdrawn once a decision has been made in the browser, mid-wait.
        line.Show("waiting", null);
        await Assert.That(line.Drawn).IsEqualTo(1);

        line.Stop();
        await Assert.That(line.Drawn).IsEqualTo(0);
    }

    [Test]
    public async Task Shrinking_the_block_erases_the_rows_it_had__not_the_rows_it_will_have() {
        // The hazard the row count exists for: erasing one row after drawing two leaves the offer line
        // stranded below the spinner for the rest of the wait.
        var (line, control) = Build();

        line.Show("waiting", "t to carry on here");

        var before = control.ToString().Length;

        line.Show("waiting", null);

        var shrink = control.ToString()[before..];

        await Assert.That(shrink.Split(Clear).Length - 1).IsEqualTo(2);
        await Assert.That(shrink).Contains(Up);
    }

    [Test]
    public async Task The_cursor_is_hidden_once_and_given_back_once() {
        var (line, control) = Build();

        line.Show("waiting", null);
        line.Show("still waiting", null);
        line.Stop();

        var written = control.ToString();

        await Assert.That(written.Split(Hide).Length - 1).IsEqualTo(1);
        await Assert.That(written.Split(Show).Length - 1).IsEqualTo(1);
        await Assert.That(written.IndexOf(Show, StringComparison.Ordinal))
                    .IsGreaterThan(written.IndexOf(Hide, StringComparison.Ordinal));
    }

    [Test]
    public async Task Stopping_twice_does_not_give_the_cursor_back_twice() {
        // The import stops the block mid-wait and the leg's finally stops it again on the way out.
        var (line, control) = Build();

        line.Show("waiting", null);
        line.Stop();
        line.Stop();

        await Assert.That(control.ToString().Split(Show).Length - 1).IsEqualTo(1);
    }

    [Test]
    public async Task Restarting_after_the_import_hides_the_cursor_again() {
        var (line, control) = Build();

        line.Show("waiting", null);
        line.Stop();
        line.Show("waiting", null);

        await Assert.That(control.ToString().Split(Hide).Length - 1).IsEqualTo(2);
        await Assert.That(line.Drawn).IsEqualTo(1);
    }

    // A redirected stream gets no escape sequences at all: they are noise in a log, and the transitions
    // are printed plainly instead.
    [Test]
    public async Task Nothing_is_drawn_or_hidden_without_a_terminal() {
        var (line, control) = Build(tty: false);

        line.Show("waiting", "t to carry on here");
        line.Stop();

        await Assert.That(control.ToString()).IsEmpty();
        await Assert.That(line.Drawn).IsEqualTo(0);
    }

    // A four-cell prefix sits before a character of the wait, so below its own width the prefix wraps
    // however hard the text is clipped, and the row count is a lie from then on. Nothing is drawn rather
    // than drawn wrong - the permanent lines still print, as they do with output redirected.
    [Test]
    public async Task A_terminal_too_narrow_for_the_prefix_is_not_drawn_on() {
        var (line, control) = Build(width: 3);

        line.Show("waiting", "t to carry on here");

        await Assert.That(line.Drawn).IsEqualTo(0);
        await Assert.That(control.ToString()).DoesNotContain(Hide);
    }

    // There is no safe width to guess: one wider than the terminal wraps, and one narrower is the same
    // lie the caller is being spared.
    [Test]
    public async Task A_width_that_cannot_be_read_is_not_guessed_at() {
        var (line, control) = Build(width: null);

        line.Show("waiting", null);

        await Assert.That(line.Drawn).IsEqualTo(0);
        await Assert.That(control.ToString()).DoesNotContain(Hide);
    }

    [Test]
    public async Task Widening_the_terminal_past_the_minimum_starts_drawing_again() {
        var control = new StringWriter();
        int? width  = 3;
        var line    = new TerminalWaitLine(tty: true, control, () => width);

        line.Show("waiting", null);
        await Assert.That(line.Drawn).IsEqualTo(0);

        width = 80;
        line.Show("waiting", null);

        await Assert.That(line.Drawn).IsEqualTo(1);
        await Assert.That(control.ToString()).Contains(Hide);
    }

    // Narrowing mid-wait must take the block down and give the cursor back, not leave two rows recorded
    // against a terminal that can no longer hold one.
    [Test]
    public async Task Narrowing_mid_wait_takes_the_block_down_and_restores_the_cursor() {
        var control = new StringWriter();
        int? width  = 80;
        var line    = new TerminalWaitLine(tty: true, control, () => width);

        line.Show("waiting", "t to carry on here");
        await Assert.That(line.Drawn).IsEqualTo(2);

        width = 3;
        line.Show("waiting", "t to carry on here");

        await Assert.That(line.Drawn).IsEqualTo(0);
        await Assert.That(control.ToString().Split(Show).Length - 1).IsEqualTo(1);
    }

    [Test]
    public async Task Disposing_gives_the_cursor_back() {
        var (line, control) = Build();

        line.Show("waiting", null);
        line.Dispose();

        await Assert.That(control.ToString()).Contains(Show);
    }
}
