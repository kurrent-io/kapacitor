using System.Text.Json.Nodes;
using kapacitor.Commands;

namespace kapacitor.Tests.Unit;

public class MergeHookEventTests {
    [Test]
    public async Task PreservesNonKapacitorHooksInSameGroup() {
        var allHooks = new JsonObject {
            ["SessionStart"] = new JsonArray {
                (JsonNode)new JsonObject {
                    ["hooks"] = new JsonArray {
                        (JsonNode)new JsonObject {
                            ["type"]    = "command",
                            ["command"] = "kapacitor session-start",
                            ["timeout"] = 5
                        },
                        (JsonNode)new JsonObject {
                            ["type"]    = "command",
                            ["command"] = "my-custom-tool session-start",
                            ["timeout"] = 5
                        }
                    }
                }
            }
        };

        var newGroups = new JsonArray {
            (JsonNode)SetupCommand.MakeHookGroup("kapacitor session-start", timeout: 5, async_: true)
        };

        SetupCommand.MergeHookEvent(allHooks, "SessionStart", newGroups);

        var result = allHooks["SessionStart"]!.AsArray();

        // Should have 2 groups: the preserved custom hook group + the new kapacitor group
        await Assert.That(result.Count).IsEqualTo(2);

        // First group should be the preserved custom hook (kapacitor one removed)
        var preservedGroup = result[0]!.AsObject();
        var preservedHooks = preservedGroup["hooks"]!.AsArray();
        await Assert.That(preservedHooks.Count).IsEqualTo(1);
        await Assert.That(preservedHooks[0]!["command"]!.GetValue<string>()).IsEqualTo("my-custom-tool session-start");

        // Second group should be the new kapacitor hook
        var newGroup = result[1]!.AsObject();
        var newHooks = newGroup["hooks"]!.AsArray();
        await Assert.That(newHooks[0]!["command"]!.GetValue<string>()).IsEqualTo("kapacitor session-start");
    }

    [Test]
    public async Task RemovesGroupEntirelyWhenOnlyKapacitorHooks() {
        var allHooks = new JsonObject {
            ["Stop"] = new JsonArray {
                (JsonNode)new JsonObject {
                    ["hooks"] = new JsonArray {
                        (JsonNode)new JsonObject {
                            ["type"]    = "command",
                            ["command"] = "kapacitor stop",
                            ["timeout"] = 5
                        }
                    }
                }
            }
        };

        var newGroups = new JsonArray {
            (JsonNode)SetupCommand.MakeHookGroup("kapacitor stop", timeout: 5)
        };

        SetupCommand.MergeHookEvent(allHooks, "Stop", newGroups);

        var result = allHooks["Stop"]!.AsArray();

        // Old group removed (was all-kapacitor), only the new one remains
        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0]!["hooks"]![0]!["command"]!.GetValue<string>()).IsEqualTo("kapacitor stop");
    }

    [Test]
    public async Task PreservesUnrelatedEventHooks() {
        var allHooks = new JsonObject {
            ["SessionStart"] = new JsonArray {
                (JsonNode)new JsonObject {
                    ["hooks"] = new JsonArray {
                        (JsonNode)new JsonObject {
                            ["type"]    = "command",
                            ["command"] = "other-tool do-stuff",
                            ["timeout"] = 10
                        }
                    }
                }
            }
        };

        var newGroups = new JsonArray {
            (JsonNode)SetupCommand.MakeHookGroup("kapacitor session-start", timeout: 5)
        };

        SetupCommand.MergeHookEvent(allHooks, "SessionStart", newGroups);

        var result = allHooks["SessionStart"]!.AsArray();

        // Both the existing unrelated hook group and the new kapacitor group
        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result[0]!["hooks"]![0]!["command"]!.GetValue<string>()).IsEqualTo("other-tool do-stuff");
        await Assert.That(result[1]!["hooks"]![0]!["command"]!.GetValue<string>()).IsEqualTo("kapacitor session-start");
    }

    [Test]
    public async Task CreatesEventWhenNoneExists() {
        var allHooks = new JsonObject();

        var newGroups = new JsonArray {
            (JsonNode)SetupCommand.MakeHookGroup("kapacitor notification", timeout: 5)
        };

        SetupCommand.MergeHookEvent(allHooks, "Notification", newGroups);

        var result = allHooks["Notification"]!.AsArray();
        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0]!["hooks"]![0]!["command"]!.GetValue<string>()).IsEqualTo("kapacitor notification");
    }

    [Test]
    public async Task FiltersPersistSessionIdAndSetTitlePromptScripts() {
        var allHooks = new JsonObject {
            ["SessionStart"] = new JsonArray {
                (JsonNode)new JsonObject {
                    ["hooks"] = new JsonArray {
                        (JsonNode)new JsonObject {
                            ["type"]    = "command",
                            ["command"] = "/usr/local/lib/kapacitor/plugin/hooks/persist-session-id.sh",
                            ["timeout"] = 3
                        }
                    }
                }
            },
            ["UserPromptSubmit"] = new JsonArray {
                (JsonNode)new JsonObject {
                    ["hooks"] = new JsonArray {
                        (JsonNode)new JsonObject {
                            ["type"]    = "command",
                            ["command"] = "/some/path/set-title-prompt.sh",
                            ["timeout"] = 2
                        }
                    }
                }
            }
        };

        SetupCommand.MergeHookEvent(allHooks, "SessionStart", new JsonArray {
            (JsonNode)SetupCommand.MakeHookGroup("kapacitor session-start", timeout: 5)
        });
        SetupCommand.MergeHookEvent(allHooks, "UserPromptSubmit", new JsonArray {
            (JsonNode)SetupCommand.MakeHookGroup("/new/path/set-title-prompt.sh", timeout: 2)
        });

        var sessionStart = allHooks["SessionStart"]!.AsArray();
        await Assert.That(sessionStart.Count).IsEqualTo(1);
        await Assert.That(sessionStart[0]!["hooks"]![0]!["command"]!.GetValue<string>()).IsEqualTo("kapacitor session-start");

        var userPrompt = allHooks["UserPromptSubmit"]!.AsArray();
        await Assert.That(userPrompt.Count).IsEqualTo(1);
        await Assert.That(userPrompt[0]!["hooks"]![0]!["command"]!.GetValue<string>()).IsEqualTo("/new/path/set-title-prompt.sh");
    }

    [Test]
    public async Task DropsMalformedEntriesWithoutHooksArray() {
        var allHooks = new JsonObject {
            ["SessionStart"] = new JsonArray {
                // Flat object without nested hooks array — malformed
                (JsonNode)new JsonObject {
                    ["type"]    = "command",
                    ["command"] = "some-tool start",
                    ["timeout"] = 5
                },
                // Properly structured group
                (JsonNode)new JsonObject {
                    ["hooks"] = new JsonArray {
                        (JsonNode)new JsonObject {
                            ["type"]    = "command",
                            ["command"] = "other-tool start",
                            ["timeout"] = 5
                        }
                    }
                }
            }
        };

        var newGroups = new JsonArray {
            (JsonNode)SetupCommand.MakeHookGroup("kapacitor session-start", timeout: 5)
        };

        SetupCommand.MergeHookEvent(allHooks, "SessionStart", newGroups);

        var result = allHooks["SessionStart"]!.AsArray();

        // Malformed entry dropped, properly structured group + new kapacitor group
        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result[0]!["hooks"]![0]!["command"]!.GetValue<string>()).IsEqualTo("other-tool start");
        await Assert.That(result[1]!["hooks"]![0]!["command"]!.GetValue<string>()).IsEqualTo("kapacitor session-start");
    }
}
