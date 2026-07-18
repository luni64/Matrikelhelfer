// Content script for Ancestry: injects a "Matricula-Zitat einfügen" button
// into the "Quellenangabe hinzufügen" dialog and fills it from the current
// citation in storage.local. Replaces the WPF app's UI-Automation-based
// AncestrySiteFiller - here the hidden <select> is just a DOM element, so
// selecting a source is an assignment instead of coordinate clicking.
//
// NOTE: the field lookups (label texts, #addCitationSourceSelectorId) come
// from the German Ancestry UI and the UIA-era findings; they need one live
// verification pass against the real dialog DOM.
"use strict";

(() => {
  const SELECT_ID = "addCitationSourceSelectorId";
  const BUTTON_ID = "mh-fill-button";
  const PANEL_ID = "mh-feedback-panel";
  const DETAILS_LABEL = "Details der Quellenangabe";
  const URL_LABEL = "Internetadresse der Quellenangabe";
  const TITLE_LABEL = "Titel der Quelle";
  const NEW_SOURCE_TAB_LABEL = "Neue Quelle erstellen";

  // Auto-select only on a `certain` match (parish + signatur, see
  // MH.evaluate) that is also a clear winner. Parish + book type alone -
  // even with a matching year - shows the candidate list instead: parishes
  // often have several same-type volumes and the years in the
  // crowd-sourced titles are unreliable, so a wrong auto-pick is worse
  // than one extra click.
  const CLEAR_WINNER_MARGIN = 20;

  function log(...args) {
    console.info("[Matricula Helper]", ...args);
  }

  // React tracks its own notion of an input's value, so a plain `el.value =`
  // gets reverted on the next render. Assign through the native setter and
  // fire an input event so the page's onChange handlers see the new value.
  function setInputValue(el, text) {
    const proto = el instanceof HTMLTextAreaElement
      ? HTMLTextAreaElement.prototype
      : HTMLInputElement.prototype;
    Object.getOwnPropertyDescriptor(proto, "value").set.call(el, text);
    el.dispatchEvent(new Event("input", { bubbles: true }));
    el.dispatchEvent(new Event("change", { bubbles: true }));
  }

  // Ancestry marks required fields with a trailing "*" in the label text.
  // `quiet` suppresses the not-found log for polling callers.
  function findControlByLabel(labelText, quiet = false) {
    for (const label of document.querySelectorAll("label")) {
      const text = label.textContent.trim().replace(/\*\s*$/, "").trim();
      if (text === labelText) {
        if (label.htmlFor) {
          const el = document.getElementById(label.htmlFor);
          if (el) {
            return el;
          }
        }
        const el = label.parentElement?.querySelector("input, textarea, select");
        if (el) {
          return el;
        }
      }
    }
    if (!quiet) {
      log(`control for label '${labelText}' not found`);
    }
    return null;
  }

  function scoreOptions(select, citation) {
    return [...select.options]
      .filter((option) => option.textContent.trim() && option.textContent.trim() !== "Auswählen")
      .map((option) => ({ option, ...MH.evaluate(option.textContent, citation) }))
      .sort((a, b) => b.score - a.score);
  }

  function applySelection(select, option) {
    select.value = option.value;
    select.dispatchEvent(new Event("change", { bubbles: true }));

    // The visible "Titel der Quelle" control is a separate display input
    // (class 'calloutTrigger') paired with the hidden select, and Ancestry
    // doesn't necessarily sync its text on a programmatic select change -
    // mirror the chosen title into it so the selection is visible.
    const trigger = document.querySelector("input.calloutTrigger");
    if (trigger) {
      setInputValue(trigger, option.textContent.trim());
    } else {
      log("input.calloutTrigger not found - selection set on the hidden select but may not show in the dialog");
    }
  }

  // --- remembered book->source mappings (port of the WPF SourceMappingStore
  // idea): picking from the candidate list stores the choice, and the same
  // book auto-fills on the next visit. Keyed on the citation's book-level
  // identity, persisted in storage.local.

  function bookKey(citation) {
    return [citation.pfarrei, citation.buchtyp, citation.signatur]
      .map((s) => MH.normalizeUmlauts(s ?? "").toLowerCase().trim())
      .join("|");
  }

  async function getMappings() {
    const { sourceMappings } = await MH.api.storage.local.get("sourceMappings");
    return sourceMappings ?? {};
  }

  async function rememberMapping(citation, optionText) {
    const mappings = await getMappings();
    mappings[bookKey(citation)] = optionText;
    await MH.api.storage.local.set({ sourceMappings: mappings });
  }

  async function forgetMapping(citation) {
    const mappings = await getMappings();
    delete mappings[bookKey(citation)];
    await MH.api.storage.local.set({ sourceMappings: mappings });
  }

  function waitFor(probe, tries = 20, intervalMs = 100) {
    return new Promise((resolve) => {
      const attempt = () => {
        const result = probe();
        if (result || tries-- <= 0) {
          resolve(result ?? null);
          return;
        }
        setTimeout(attempt, intervalMs);
      };
      attempt();
    });
  }

  // Switch to Ancestry's "Neue Quelle erstellen" tab and pre-fill "Titel
  // der Quelle" with the book-level citation title (the details/URL fields
  // sit outside the tabs and keep the values fill() already set). On this
  // tab the title field is a plain text input - distinguishable from the
  // existing-source tab's dropdown trigger by the calloutTrigger class.
  async function createNewSource(citation) {
    const matches = [...document.querySelectorAll("a, button, li, span, [role='tab']")]
      .filter((el) => el.textContent.trim() === NEW_SOURCE_TAB_LABEL);
    const tab = matches.at(-1); // last match in document order = innermost
    if (!tab) {
      showStatus('Tab "Neue Quelle erstellen" nicht gefunden.');
      return;
    }
    tab.click();

    const title = await waitFor(() => {
      const el = findControlByLabel(TITLE_LABEL, true);
      return el && el.tagName === "INPUT" && !el.classList.contains("calloutTrigger") ? el : null;
    });
    if (!title) {
      showStatus('Titelfeld auf dem Tab "Neue Quelle erstellen" nicht gefunden.');
      return;
    }
    setInputValue(title, MH.citationTitle(citation));
    showStatus(`Neuer Quellentitel eingetragen: ${MH.citationTitle(citation)}`);
  }

  // Feedback panel injected right below the button - status messages and,
  // when no confident match exists, a clickable list of similar sources
  // (the in-page equivalent of the WPF app's SourcePickerWindow).
  function panel() {
    let p = document.getElementById(PANEL_ID);
    if (!p) {
      p = document.createElement("div");
      p.id = PANEL_ID;
      p.style.cssText =
        "margin:4px 0 8px;padding:6px 8px;border:1px solid #d8c9a3;border-radius:4px;" +
        "background:#fdf8ee;color:#4a3a1a;font-size:13px;";
      document.getElementById(BUTTON_ID)?.insertAdjacentElement("afterend", p);
    }
    p.replaceChildren();
    return p;
  }

  function showStatus(text) {
    const div = document.createElement("div");
    div.textContent = text;
    panel().append(div);
  }

  function actionLink(text, onClick) {
    const a = document.createElement("a");
    a.href = "#";
    a.textContent = text;
    a.style.cssText = "display:inline-block;margin-top:6px;color:#7a5c2e;text-decoration:underline;cursor:pointer;";
    a.addEventListener("click", (e) => {
      e.preventDefault();
      onClick();
    });
    return a;
  }

  function showCandidateList(select, scored, citation) {
    const p = panel();
    const similar = scored.filter((s) => s.score > 0).slice(0, 8);

    const msg = document.createElement("div");
    msg.style.marginBottom = "4px";
    msg.textContent = similar.length
      ? "Keine eindeutig passende Quelle gefunden. Ähnliche Einträge (anklicken zum Übernehmen):"
      : "Keine ähnliche Quelle in der Liste gefunden.";
    p.append(msg);

    for (const { option, score } of similar) {
      const row = document.createElement("div");
      row.textContent = option.textContent.trim();
      row.title = `Ähnlichkeit: ${score}`;
      row.style.cssText = "padding:3px 6px;cursor:pointer;border-radius:3px;";
      row.addEventListener("mouseenter", () => { row.style.background = "#efe3c5"; });
      row.addEventListener("mouseleave", () => { row.style.background = ""; });
      row.addEventListener("click", async () => {
        applySelection(select, option);
        await rememberMapping(citation, option.textContent.trim());
        showStatus(`Quelle ausgewählt und für dieses Buch gemerkt: ${option.textContent.trim()}`);
      });
      p.append(row);
    }

    p.append(actionLink("Neue Quelle erstellen und Titel eintragen", () => createNewSource(citation)));
  }

  async function fill() {
    const { currentCitation } = await MH.api.storage.local.get("currentCitation");
    if (!currentCitation) {
      showStatus("Kein Zitat vorhanden - zuerst eine Kirchenbuchseite auf data.matricula-online.eu öffnen.");
      return;
    }
    log("filling from citation:", currentCitation);

    const details = findControlByLabel(DETAILS_LABEL);
    if (details) {
      setInputValue(details, MH.pageDescription(currentCitation));
    }
    const urlField = findControlByLabel(URL_LABEL);
    if (urlField) {
      setInputValue(urlField, currentCitation.url);
    }

    const select = document.getElementById(SELECT_ID);
    if (!select) {
      log(`#${SELECT_ID} not found - is the 'Vorhandene Quelle auswählen' tab active?`);
      showStatus("Quellen-Auswahlliste nicht gefunden (ist der Tab \"Vorhandene Quelle auswählen\" aktiv?).");
      return;
    }
    if (select.options.length <= 1) {
      log(`#${SELECT_ID} has only ${select.options.length} option(s) - source list not loaded?`);
      showStatus("Die Quellenliste ist noch leer - Dropdown einmal öffnen und erneut versuchen.");
      return;
    }

    // A previously remembered pick for this book wins over scoring.
    const remembered = (await getMappings())[bookKey(currentCitation)];
    if (remembered) {
      const option = [...select.options].find((o) => o.textContent.trim() === remembered);
      if (option) {
        applySelection(select, option);
        const p = panel();
        const div = document.createElement("div");
        div.textContent = `Gemerkte Quelle ausgewählt: ${remembered}`;
        p.append(div, actionLink("Zuordnung vergessen und neu suchen", async () => {
          await forgetMapping(currentCitation);
          fill();
        }));
        return;
      }
      log(`remembered source '${remembered}' is no longer in the list - falling back to matching`);
    }

    const scored = scoreOptions(select, currentCitation);
    log(`top matches out of ${select.options.length} options:`,
      scored.slice(0, 5).map((s) => `[${s.score}] ${s.option.textContent.trim()}`));

    // Auto-select needs both a confident score and a clear winner: several
    // books of the same parish and type (e.g. three Ruhpolding Trauungen
    // volumes) can tie near the threshold with only the unreliable year to
    // tell them apart - in that case the user picks from the list instead.
    const best = scored[0];
    const runnerUp = scored[1];
    if (best && best.certain &&
        (!runnerUp || best.score - runnerUp.score >= CLEAR_WINNER_MARGIN)) {
      applySelection(select, best.option);
      showStatus(`Quelle ausgewählt: ${best.option.textContent.trim()}`);
    } else {
      showCandidateList(select, scored, currentCitation);
    }
  }

  function injectButton() {
    const select = document.getElementById(SELECT_ID);
    if (!select || document.getElementById(BUTTON_ID)) {
      return;
    }
    const button = document.createElement("button");
    button.id = BUTTON_ID;
    button.type = "button";
    button.textContent = "Matricula-Zitat einfügen";
    button.style.cssText =
      "margin:8px 0;padding:6px 12px;border:1px solid #7a5c2e;border-radius:4px;" +
      "background:#f5e9d0;color:#4a3a1a;font-weight:600;cursor:pointer;";
    button.addEventListener("click", (e) => {
      e.preventDefault();
      fill();
    });

    // Anchor near the source selector; the exact container is one of the
    // things to verify against the live dialog.
    const host = select.closest("form") ?? select.parentElement;
    host.insertBefore(button, host.firstChild);
    log("button injected");
  }

  // The dialog is created on demand - watch for it appearing (and for it
  // being re-created, which discards a previously injected button).
  const observer = new MutationObserver(() => injectButton());
  observer.observe(document.body, { childList: true, subtree: true });
  injectButton();
})();
