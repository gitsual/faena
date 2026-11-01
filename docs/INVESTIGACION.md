*[Leer en español](INVESTIGACION.es.md)*

# Research: agents that drive applications locally (2026-10-28)

Done with a research workflow: 5 search fronts, 23 sources read, 106 claims extracted, 25 verified by 3 adversarial votes (21 confirmed, 4 refuted).

## Summary

What was verified points to five techniques faena can adopt. (1) Represent the page as a text snapshot of the accessibility tree with ephemeral refs (Playwright MCP): it is cheap in tokens, points at the exact element and, when a ref expires, gives an explicit error that can be used to recover. On top of that, task-dependent distilled views and an "observation of the change" after each action (Agent-E). (2) On the Windows desktop, the strongest pattern is UFO2's hybrid. First, control detection combines UIA with vision (OmniParser): with o1 on OSWorld-W, 28.6 % versus 24.5 % with UIA only and 14.3 % with vision only. Second, native APIs are preferred and the GUI is the fallback: +6.1 to +8.2 points and, with o1, from 16.0 to 6.6 steps. Third, it can run on a nested desktop so as not to fight the user for the mouse. CoAct-1 confirms that combining code and GUI beats either one alone (60.76 % versus 50.68 % and 35.73 % on OSWorld). (3) In procedural memory, the 2025 trend is to represent skills as parameterized programs (ASI, SkillWeaver) rather than text notes. A skill only enters the library after it is verified: a judge considers the task solved, the skill is called and every call changes the environment. With that, SkillWeaver improves by 31.8 % to 39.8 % relative. Stagehand's cache replays the steps without an LLM and, on a cache miss, calls the model again; it is the direct model for faena's TOML skills. On safety and human-in-the-loop (4) and on small local models for GUIs (5), no claim passed verification, so those recommendations remain unsupported in this report.

## Verified findings

### (high, vote 3-0 (x3))

Web page representation: use a text snapshot of the accessibility tree where each exposed node carries an ephemeral ref (e5, f2e7 inside iframes). The ref is unique within the snapshot and is valid until the page changes. Actions accept a ref (preferred) or a selector, and an expired ref returns an explicit error ("Ref not found... Try capturing new snapshot") that serves as a recovery signal. Compared with screenshots, it costs fewer tokens, points at the exact element instead of approximate coordinates and needs no vision model. Recommendation for faena: have the observe→decide JSON loop receive refs from a Playwright ARIA snapshot (in .NET, Locator.AriaSnapshotAsync or equivalent). If a ref expires, take the snapshot again automatically instead of failing. Screenshots are kept only for canvases or graphics.

Sources: https://playwright.dev/mcp/snapshots

Caveat: Three claims from the official Playwright MCP documentation, all 3-0. Caveat: on large pages the snapshot gets expensive and piles up in the context over several steps; there are reports of about 114k tokens on Salesforce. The documentation itself recommends limiting it with depth/target or browser_find.

### (medium, vote 3-0 (x3))

Task-dependent distilled DOM and observation of the change after each action (Agent-E). There are three DOM views: text_only, input_fields and all_fields. The agent picks the one that fits the step and switches to another if it does not work. Each action reports in words what changed (for example, that a popup opened), detected by code with MutationObserver and aria-expanded, without an LLM. Unlike Reflexion, this happens on every action and not only after a failure. Agent-E reached 73.1 % on WebVoyager (643 tasks), versus 52.6 % for WILBUR and 57.1 % for the WebVoyager agent, although it stayed at 27.3 % on Booking and 35.7 % on Google Flights. Recommendation: add to faena's observation a diff or change field computed without a model (on the web, nodes added to or removed from the ARIA tree; on the desktop, UIA StructureChanged events or new windows). And offer the model filtered views (input fields only or text only) to reduce tokens with local qwen3-coder.

Sources: https://arxiv.org/html/2407.13032v1

Caveat: A single primary source, from July 2024 (before the 2025-2026 window). The figures are self-reported and already surpassed, although the techniques are still valid. Votes 3-0.

### (high, vote 3-0 (x2))

Windows desktop: hybrid UIA + vision control detection. UFO2 merges UIA metadata with OmniParser-v2 for non-standard controls. With o1 on OSWorld-W: UIA only, 24.5 %; OmniParser only, 14.3 %; hybrid, 28.6 %. On WAA with o1: 27.9 % versus 25.3 % and 20.8 %. The hybrid recovers 9.86 % of UIA's failures. Recommendation: keep UIA as faena's main path and add a visual fallback (a screenshot parser or a local VLM) only when UIA does not expose the control, for example when a custom element has an empty name or the tree is empty.

Sources: https://arxiv.org/abs/2504.14603v2, https://microsoft.github.io/UFO/ufo2/overview

Caveat: The paper and Microsoft's official documentation agree, 3-0. Caveats: with GPT-4o on WAA, vision alone (26.6 %) beat UIA alone (23.4 %), and on OSWorld-W it was a tie (22.4 %). The hybrid mode is configurable and is not on by default (control_backend: uia).

### (high, vote 3-0 (x4))

Hybrid API + GUI action: priority to the native API, with the GUI as fallback. UFO2 uses xlwings for Excel, win32com for Outlook and python-pptx for PowerPoint, and only falls back to the GUI when no API covers the step. On 27 OSWorld office tasks: with GPT-4o, from 16.3 % to 22.4 %; with o1, from 16.3 % to 24.5 %, and from 16.0 to 6.6 steps on average. CoAct-1 (Salesforce, 2025-2026) generalizes the idea: an orchestrator assigns each subtask either to a GUI operator (VLM) or to a programmer that writes and runs Python or Bash. On OSWorld: programmer only, 35.73 % (1.14 steps); GUI only, 50.68 % (11.20 steps); combined, 60.76 % (10.15 steps). Recommendation: add to faena an "api" or "script" action type, with COM/Office Interop from C# or PowerShell in a restricted environment, that the model can choose before the GUI. It must go through the irreversible-action guard just like GUI actions.

Sources: https://arxiv.org/abs/2504.14603v2, https://microsoft.github.io/UFO/ufo2/overview, https://arxiv.org/pdf/2508.03923v3

Caveat: Two independent systems (Microsoft and Salesforce) reach the same conclusion, 3-0. Caveats: small samples (27 tasks and 12 hand-written APIs), results measured by the authors themselves, and CoAct-1 evaluated on Ubuntu, not Windows.

### (medium, vote 3-0 (x2))

Windows desktop benchmarks. UFO2 reaches 27.9 % on WindowsAgentArena and 28.6 % on OSWorld-W with GPT-4o (30.5 % and 32.7 % with o1). The baselines: Operator, 20.8 % and 14.3 %; original UFO, 19.5 % and 12.2 %; Agent S, 18.2 % and 12.2 %; NAVI, 13.3 % and 10.2 %. All limited to 30 steps and measured by the authors. Windows Agent Arena (ICML 2025) has over 150 tasks (154) on real Windows and is the natural benchmark for evaluating faena.

Sources: https://arxiv.org/abs/2504.14603v2, https://icml.cc/virtual/2025/poster/45035

Caveat: The sources are primary, but the figures are self-reported and from April 2025, so they have probably been surpassed. The claim that the best WAA agent reaches 19.5 % versus 74.5 % for humans was refuted.

### (medium, vote 3-0)

Running in the background. UFO2 runs the agent on a nested virtual desktop (Picture-in-Picture through a loopback Remote Desktop), so the user keeps working while the agent automates, without competing for mouse or keyboard. Recommendation: study it so that faena can work without blocking the user. A local RDP session to the same machine has its own interactive desktop, which UIA can drive.

Sources: https://microsoft.github.io/UFO/ufo2/overview

Caveat: Only UFO's documentation confirms it. The GitHub README does not mention PiP, and it is not stated whether it is on by default or what configuration it needs.

### (medium, vote 3-0 (x2))

Action cache and replay without an LLM (Stagehand v3), a direct equivalent of the TOML skills. With cacheDir, the first run saves each step of act() and agent(), and later runs replay them without calling the LLM or spending tokens, both locally and on Browserbase. In the server cache, the key combines the instruction, the page's content or structure, the URL and the options. If the page changes, the key does not match, the LLM is called again and the new entry serves the following runs, which in practice is self-healing. Recommendation: store in each TOML step a fingerprint of the expected state (hash of the ARIA or UIA subtree around the target, normalized URL or window title). When replaying, if the fingerprint does not match or the locator fails, call the model only for that step, rewrite the step in the TOML and continue.

Sources: https://docs.stagehand.dev/v3/best-practices/caching.md

Caveat: Official documentation, 3-0. But automatic LLM retry is only documented for Browserbase's server cache; the local cache has to be cleared by hand. It was refuted (0-3) that the key uses the variables' names rather than their values, so it cannot be claimed that one entry serves several parameters.

### (high, vote 3-0 (x4))

Procedural memory: skills as parameterized, verified programs. In ASI (CMU, 2025) skills are Python functions with parameters and a docstring that wrap primitives (click, fill, hover), for example search_product(name: str). They have 2 to 5 steps, can call other skills and enter the agent's action space, unlike AWM's free text. A skill only enters the library if, when the rewritten trajectory is run again, (1) an LLM judge considers the task solved, (2) at least one new skill is called and (3) every call changes the environment. SkillWeaver (April 2025) proposes skills, practises them and distils them into APIs, with relative improvements of 31.8 % on WebArena and 39.8 % on real websites. Recommendation: expose faena's TOML skills as actions the model can call (skill: name with parameters and a description). Validate them before saving them with a replay plus a judge plus the check that every step changed the state. And allow them to be composed.

Sources: https://arxiv.org/abs/2504.06821, https://arxiv.org/abs/2504.07079

Caveat: Two independent primary papers from 2025, 3-0. Figures measured by the authors; relative gains over low baselines amount to fewer absolute points. The transfer from a strong agent to a weak one of up to 54.3 % was refuted (1-2), so it has not been shown that building skills with Claude and replaying them with local qwen works equally well.

## Limitations

No verified claim covers safety and human-in-the-loop (prompt injection from pages, sandboxing, confirmation of sensitive actions) or small local models for GUIs (UI-TARS, Qwen2.5/3-VL, OpenCUA, GUI-Actor) with their figures. Those two parts of the question (points 4 and 5) remain without verified support, and any recommendation on them would have to come from further research. Nor are there verified claims about browser-use, Skyvern, LaVague, Nanobrowser, Agent S2, OS-Copilot, Windows-MCP or Claude's or OpenAI's computer use, except Operator as a baseline. Almost all figures are published by the authors themselves in their papers or documentation, with small samples (27 office tasks, about 154 WAA tasks). Several are from 2024 or early 2025 (Agent-E, July 2024; UFO2, April 2025) and the state of the art moves fast. Four claims were refuted: Stagehand's cache keyed by variable name, the 19.5 % versus 74.5 % for humans on WAA, the comparison of CoAct-1 with Agent S2.5 and GTA-1, and the transfer of skills to weak models (54.3 %). CoAct-1 and SkillWeaver were evaluated on Linux or the web, not on Windows, so applying them to faena is by analogy.

## Open questions

- Which small local model (UI-TARS, Qwen3-VL or OpenCUA, 7 to 32B) performs best on WindowsAgentArena or OSWorld on consumer hardware, and can it replace qwen3-coder:30b as a visual fallback when UIA does not expose the control?
- What concrete defences against prompt injection from page or window content, and what confirmation policies, do the agents of 2025-2026 use (Operator, Claude's computer use, browser-use), and which of them can be brought into faena's guard?
- Do skills learned with a strong model (Claude) work when replayed or repaired with a weak local model? SkillWeaver's transfer claim was refuted.
- How to keep a stable fingerprint for invalidating the skill cache on UIA and ARIA without the changing runtime state causing constant cache misses?

## Refuted

- (0-3) When an action uses variables, Stagehand builds the cache key from the variable names, not their values, so a single cached entry serves many parameterized inputs. This is equivalent to faena's TOML skills with parameters. — https://docs.stagehand.dev/v3/best-practices/caching.md
- (0-3) In WAA, the best multimodal OS agent evaluated reaches only a 19.5% success rate. Humans reach 74.5%, which leaves a large gap for Windows desktop agents. — https://icml.cc/virtual/2025/poster/45035
- (0-3) On OSWorld, CoAct-1 reaches a 60.76% success rate (100-step budget) and needs 10.15 steps per task on average. Agent S2.5 w/ o3 scores 55.98% and GTA-1 w/ o3 scores 53.07%. GTA-1 averages 15.22 steps and UI-TARS 14.90. — https://arxiv.org/pdf/2508.03923v3
- (1-2) Skill APIs synthesized by a strong agent transfer to weaker agents and improve their WebArena success by up to 54.3%. This supports building a skill library with a strong model (e.g. Claude) and replaying it with a weaker local model. — https://arxiv.org/abs/2504.07079
