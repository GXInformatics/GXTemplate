using System;
using System.IO;
using FluentAssertions;
using Jint;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Server.UI.IntegrationTests;

/// <summary>
/// What the application does when the Blazor circuit goes down - driven through the real
/// <c>wwwroot/js/gxReconnect.js</c>, not a description of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This suite exists because the defect it pins could not be tested where the code used to
/// live.</b> The reconnect logic was an inline <c>&lt;script&gt;</c> inside
/// <c>ReconnectModal.razor</c>: bUnit renders markup and never executes script, and the logic
/// cannot move to C# because it has to keep working while the circuit - and therefore all C# - is
/// gone. So the only assertions available were that the HTML existed, which was never in doubt.
/// Pass 41 moved the decisions into a file and runs them here on Jint, a pure-C# JavaScript
/// interpreter: no Node, no browser, no native dependency.
/// </para>
/// <para>
/// <b>The property, not the implementation.</b> Each test sets the CSS class Blazor itself sets -
/// read off <c>blazor.web.js</c> at .NET 10, where the reconnect display emits seven states and not
/// the three the old script knew - and then asserts what happened to the page. No test asserts
/// which class a branch reads. The one that matters is
/// <see cref="ATransientDrop_DoesNotReloadThePage"/>: a drop Blazor recovers from on its own must
/// leave the page, and everything typed into it, untouched.
/// </para>
/// <para>
/// <b>What this cannot prove.</b> Whether a reload issued during a committed navigation wins the
/// race against that navigation is browser behaviour, and the reported symptom - login failing in
/// Firefox and not in Chromium - is exactly a difference in that behaviour. What is proved here is
/// that the reload is no longer <i>issued</i>. The browser half is on the hand-test list in the
/// Pass 41 report.
/// </para>
/// </remarks>
[TestFixture]
public class ReconnectUiTests
{
    private const string Show = "components-reconnect-show";
    private const string Retrying = "components-reconnect-retrying";
    private const string Paused = "components-reconnect-paused";
    private const string Hide = "components-reconnect-hide";
    private const string Failed = "components-reconnect-failed";
    private const string Rejected = "components-reconnect-rejected";

    /// <summary>
    /// A DOM small enough to read and real enough to matter: a class list, the four elements the
    /// script addresses by id, and a dialog that records whether it is open. Setting a class calls
    /// back into the controller exactly as the MutationObserver does in a browser, so a test says
    /// "Blazor set this class" and nothing else.
    /// </summary>
    private const string Harness = """
        var reloads = 0;
        var probeResults = [];
        var probeCalls = 0;
        var timers = [];

        function makeEl(id) {
            return { id: id, innerText: '', innerHTML: '', style: { display: '' },
                     addEventListener: function () { }, removeEventListener: function () { } };
        }

        var els = {
            'reconnect-title': makeEl('reconnect-title'),
            'reconnect-desc': makeEl('reconnect-desc'),
            'reconnect-retry': makeEl('reconnect-retry'),
            'reconnect-resume': makeEl('reconnect-resume'),
            'reconnect-reload': makeEl('reconnect-reload')
        };

        var classes = [];
        var modal = {
            id: 'components-reconnect-modal',
            open: false,
            showModal: function () { this.open = true; },
            close: function () { this.open = false; },
            classList: {
                contains: function (c) { return classes.indexOf(c) >= 0; }
            }
        };
        els['components-reconnect-modal'] = modal;

        var document = {
            visibilityState: 'visible',
            getElementById: function (id) { return els[id] || null; },
            addEventListener: function () { },
            removeEventListener: function () { }
        };

        var controller = null;

        // Blazor clears every class before adding the next - see removeClasses() in blazor.web.js -
        // except that `retrying` is added alongside `show`. Modelled here so a test can name a
        // state and get the class shape the framework really produces.
        function blazorSets(state) {
            classes = [];
            if (state === 'retrying') { classes = ['components-reconnect-show', 'components-reconnect-retrying']; }
            else if (state !== null) { classes = [state]; }
            controller.readFromClasses();
        }

        function runTimers(times) {
            for (var n = 0; n < times; n++) {
                var snapshot = timers.slice();
                for (var i = 0; i < snapshot.length; i++) { if (snapshot[i]) snapshot[i](); }
            }
        }

        var env = {
            document: document,
            reload: function () { reloads++; },
            fetch: async function () {
                probeCalls++;
                var r = probeResults.length ? probeResults.shift() : { ok: true };
                if (r.throws) { throw new Error('unreachable'); }
                return { ok: r.ok, status: r.ok ? 200 : 503 };
            },
            setInterval: function (f) { timers.push(f); return timers.length; },
            clearInterval: function (h) { timers[h - 1] = null; },
            blazor: { reconnect: async function () { return true; },
                      resumeCircuit: async function () { return true; } }
        };
        """;

    private static Engine NewEngine()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "gxReconnect.js");
        File.Exists(path).Should().BeTrue(
            "the test must run the file the application ships, linked in by the csproj - a missing " +
            "one means the link broke and the suite would silently be testing nothing");

        var engine = new Engine(o => o.Strict(false));
        engine.SetValue("gxReconnectNoAutoStart", true);
        engine.Execute(Harness);
        engine.Execute(File.ReadAllText(path));
        engine.Execute("controller = GxReconnect.create(env);");
        return engine;
    }

    private static void Drain(Engine engine) => engine.Advanced.ProcessTasks();

    private static void Blazor(Engine engine, string? state)
    {
        engine.Execute($"blazorSets({(state is null ? "null" : $"'{state}'")});");
        Drain(engine);
    }

    private static int Reloads(Engine engine) => (int)engine.Evaluate("reloads").AsNumber();
    private static bool ModalOpen(Engine engine) => engine.Evaluate("modal.open").AsBoolean();
    private static bool Probing(Engine engine) => engine.Evaluate("controller.probing").AsBoolean();

    private static bool ButtonShown(Engine engine, string id) =>
        engine.Evaluate($"els['{id}'].style.display").AsString() != "none";

    [Test]
    public void ATransientDrop_DoesNotReloadThePage()
    {
        // THE DEFECT. A lid closing, a wifi handover, a proxy hiccup: Blazor drops, retries and
        // rejoins, and the user's half-written form is still there afterwards - because nothing
        // reloaded. The script this replaced reloaded within milliseconds of the drop, before
        // Blazor had finished its first retry, and did so in every browser.
        var engine = NewEngine();

        Blazor(engine, Show);
        Blazor(engine, Retrying);
        Blazor(engine, Hide);

        Reloads(engine).Should().Be(0,
            "a drop Blazor recovers from on its own must leave the page - and everything typed " +
            "into it - exactly as it was");
        ModalOpen(engine).Should().BeFalse("the dialog closes once the circuit is back");
        Probing(engine).Should().BeFalse("a transient state must start no server probe at all");
    }

    [Test]
    public void ADeliberateNavigation_DoesNotReloadThePage()
    {
        // THE LOGIN RACE, which is the same defect wearing different clothes. Signing in is a
        // JS-built full-page form POST (auth.js), and a full-page navigation tears the circuit
        // down, so Blazor announces a disconnection on EVERY login. The old script then probed the
        // server it was being posted to, got 200 because the server was obviously up, and reloaded
        // - racing the login POST it had just started.
        //
        // A navigating page only ever reaches the first transient state before it is gone, so
        // acting on nothing but terminal states removes the race without needing to detect unload.
        var engine = NewEngine();

        Blazor(engine, Show);

        Reloads(engine).Should().Be(0,
            "a circuit torn down by a navigation must not provoke a second, competing navigation");
    }

    [Test]
    public void AGracefulPause_OffersResume_AndDoesNotReload()
    {
        // components-reconnect-paused is the .NET 9/10 pause-and-resume feature - the sleeping-tab
        // case. It is not a failure: the server is holding the circuit and can hand it back. The
        // old script had never heard of this state and reloaded anyway.
        var engine = NewEngine();

        Blazor(engine, Show);
        Blazor(engine, Paused);

        Reloads(engine).Should().Be(0, "a paused circuit can be resumed; discarding it is a choice the user should make");
        ButtonShown(engine, "reconnect-resume").Should().BeTrue("resume is the action a paused session offers");
        Probing(engine).Should().BeFalse("a pause is not a server outage");
    }

    [Test]
    public void ASuccessfulReconnection_ClosesTheModal_EvenAfterTheServerWasSeenDown()
    {
        // The old script latched "the server went down" and then used it to OVERRIDE a successful
        // reconnection, logging "Ignored fake reconnection signal" and holding the user behind a
        // modal on a page that was working perfectly, until a probe eventually reloaded it. The
        // latch is kept - it is the only evidence that a restart is in progress - but it may no
        // longer outvote the framework telling us we are back.
        var engine = NewEngine();
        engine.Execute("probeResults = [{ throws: true }, { throws: true }];");

        Blazor(engine, Show);
        Blazor(engine, Failed);
        engine.Execute("runTimers(1);");
        Drain(engine);

        Blazor(engine, Hide);

        ModalOpen(engine).Should().BeFalse("Blazor said the circuit is back, and it is the authority on that");
        Reloads(engine).Should().Be(0, "a recovered circuit needs no reload, whatever the probe saw earlier");
        Probing(engine).Should().BeFalse("and the probe stops with it");
    }

    [Test]
    public void ATerminalFailure_OffersButtons_AndDoesNotReloadWhileTheServerWasNeverDown()
    {
        // Blazor gave up, but the server answered every probe - so the failure was on this end and
        // a reload would destroy what is on screen to fix nothing that Retry does not fix better.
        // Blazor's own default display behaves the same way: a Retry button, not an automatic
        // reload.
        var engine = NewEngine();

        Blazor(engine, Show);
        Blazor(engine, Failed);
        engine.Execute("runTimers(3);");
        Drain(engine);

        Reloads(engine).Should().Be(0,
            "the server was reachable throughout, so nothing was waiting to come back");
        ButtonShown(engine, "reconnect-retry").Should().BeTrue();
        ButtonShown(engine, "reconnect-reload").Should().BeTrue("reloading stays available - as a choice");
    }

    [Test]
    public void ATerminalFailure_ReloadsOnceTheServerHasGoneAndComeBack()
    {
        // NARROWED, NOT BROKEN. The behaviour the old script was written for is the deployment
        // case: the application is restarting behind the page and should come back with it,
        // unattended. That still happens - but only after the server has actually been observed
        // DOWN, which is what makes it a recovery rather than a reflex.
        var engine = NewEngine();
        engine.Execute("probeResults = [{ throws: true }, { ok: false }, { ok: true }];");

        Blazor(engine, Show);
        Blazor(engine, Failed);
        engine.Execute("runTimers(3);");
        Drain(engine);

        Reloads(engine).Should().Be(1,
            "a server that went away and came back is exactly the case an automatic reload is for");
    }

    [Test]
    public void ARejectedCircuit_ReloadsImmediately()
    {
        // The server has refused the circuit outright: its state is gone and no retry can bring it
        // back. Blazor's own default display reloads here and only here, and that is the behaviour
        // being matched rather than invented.
        var engine = NewEngine();

        Blazor(engine, Show);
        Blazor(engine, Rejected);

        Reloads(engine).Should().Be(1, "a rejected circuit is unrecoverable; a dead page helps nobody");
    }

    [Test]
    public void AGenuinelyDeadCircuit_StillRecovers()
    {
        // The counter-test to every assertion above. A fix that simply never reloads satisfies
        // "my work was not destroyed" perfectly and leaves the user staring at a dead page, so the
        // suite has to prove recovery is still reachable: automatically when the server comes back,
        // and by hand at any time.
        var engine = NewEngine();
        engine.Execute("probeResults = [{ throws: true }, { ok: true }];");

        Blazor(engine, Show);
        Blazor(engine, Failed);
        engine.Execute("runTimers(2);");
        Drain(engine);

        Reloads(engine).Should().Be(1, "unattended recovery still happens");

        var manual = NewEngine();
        Blazor(manual, Show);
        Blazor(manual, Failed);
        ButtonShown(manual, "reconnect-reload").Should().BeTrue(
            "and a user who does not want to wait has a button");
    }
}
