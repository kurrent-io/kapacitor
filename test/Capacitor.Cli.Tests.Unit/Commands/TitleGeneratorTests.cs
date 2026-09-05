using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class TitleGeneratorTests {
    [Test]
    public async Task IsKnownCapacitorPrompt_detects_title_prompt() {
        var sample = TitleGeneration.TitlePromptPrefix + "trailing content";

        await Assert.That(TitleGenerator.IsKnownCapacitorPrompt(sample)).IsTrue();
    }

    [Test]
    public async Task IsKnownCapacitorPrompt_detects_whats_done_prompt() {
        var sample = TitleGenerator.WhatsDonePromptPrefix + "\ntrailing content";

        await Assert.That(TitleGenerator.IsKnownCapacitorPrompt(sample)).IsTrue();
    }

    [Test]
    public async Task IsKnownCapacitorPrompt_rejects_unrelated_text() {
        await Assert.That(TitleGenerator.IsKnownCapacitorPrompt("hello world")).IsFalse();
    }
}
