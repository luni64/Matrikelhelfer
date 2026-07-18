// Shared helpers for the content scripts and the popup/sidebar page.
// Plain script (content scripts can't load ES modules), so everything hangs
// off the single MH global. Loaded before matricula.js / ancestry.js /
// popup.js via manifest ordering resp. <script> order.
"use strict";

const MH = {
  // Firefox exposes the promise-based API as `browser`, Chromium as `chrome`
  // (whose storage API is also promise-based in MV3).
  api: globalThis.browser ?? globalThis.chrome,

  // Matricula's dates are spelled-out German ("31. Dezember 1625"), always
  // ending in the 4-digit year. (Port of MatriculaInfo.ExtractYear.)
  yearOf(dateText) {
    const m = /(\d{4})\s*$/.exec(dateText ?? "");
    return m ? parseInt(m[1], 10) : null;
  },

  normalizeUmlauts(s) {
    return (s ?? "")
      .replaceAll("ä", "ae").replaceAll("ö", "oe").replaceAll("ü", "ue")
      .replaceAll("Ä", "Ae").replaceAll("Ö", "Oe").replaceAll("Ü", "Ue")
      .replaceAll("ß", "ss");
  },

  tokenize(s) {
    return MH.normalizeUmlauts(s).toLowerCase().split(/[^a-z0-9]+/).filter(Boolean);
  },

  // True if a and b are equal or one edit apart (insert/delete/substitute) -
  // enough typo tolerance for the crowd-sourced Ancestry titles ("Waliting"
  // ~ "Walting", "Traungen" ~ "Trauungen").
  withinOneEdit(a, b) {
    if (a === b) {
      return true;
    }
    const la = a.length;
    const lb = b.length;
    if (Math.abs(la - lb) > 1) {
      return false;
    }
    let i = 0;
    let j = 0;
    let edits = 0;
    while (i < la && j < lb) {
      if (a[i] === b[j]) {
        i++;
        j++;
        continue;
      }
      if (++edits > 1) {
        return false;
      }
      if (la === lb) {
        i++;
        j++;
      } else if (la > lb) {
        i++;
      } else {
        j++;
      }
    }
    return edits + (la - i) + (lb - j) <= 1;
  },

  // Word matches an option token: exactly for short words, within one edit
  // for longer ones (short words would produce too many false typo hits).
  wordInTokens(word, optionTokens) {
    return optionTokens.some((t) =>
      word.length >= 5 ? MH.withinOneEdit(word, t) : word === t);
  },

  // Parish gate: strip Matricula's diocese suffix ("Walting-EI" ->
  // "Walting"), then require a distinctive (longest) name token to appear
  // in the option - so "Eichstätt, Walting 3a-02" and the misspelled
  // "Waliting-EI" both pass, while unrelated parishes are excluded outright.
  //
  // Big-city parishes are compound: "München, Haidhausen, St. Johann". The
  // first comma-component is the city - NOT required for the gate (titles
  // may omit it), but a good ranking hint when it does appear. The gate
  // instead needs one of the church/district components to match, so other
  // parishes of the same city stay excluded.
  parishMatch(optionTokens, pfarrei) {
    const base = (pfarrei ?? "").replace(/-[A-Za-z]{1,3}$/, "");
    const longestOf = (tokens) => tokens.reduce((a, b) => (b.length > a.length ? b : a));
    const components = base.split(/[,/]/)
      .map((c) => MH.tokenize(c))
      .filter((tokens) => tokens.length > 0);
    if (components.length === 0) {
      return { matched: false, cityHint: false };
    }
    if (components.length === 1) {
      return { matched: MH.wordInTokens(longestOf(components[0]), optionTokens), cityHint: false };
    }
    const matched = components.slice(1)
      .some((tokens) => MH.wordInTokens(longestOf(tokens), optionTokens));
    const cityHint = matched && MH.wordInTokens(longestOf(components[0]), optionTokens);
    return { matched, cityHint };
  },

  // Record-type synonyms and the genealogy symbols used in source titles:
  // * = birth/baptism, oo = marriage, + = death. Keys are the (normalized,
  // lowercased) Buchtyp values Matricula uses.
  typeSynonyms: {
    taufen: { words: ["taufen", "taufbuch", "taufregister", "geburten", "geburtsregister"], symbol: /\*/ },
    trauungen: { words: ["trauungen", "heiraten", "hochzeiten", "heiratsregister", "trauungsbuch"], symbol: /(^|[\s,])oo(?=[\s,\d]|$)/ },
    sterbefaelle: { words: ["sterbefaelle", "sterben", "tote", "beerdigungen", "sterberegister", "sterbebuch", "begraebnisse"], symbol: /\+/ },
  },

  typeMatchScore(optionText, optionTokens, buchtyp) {
    const key = MH.normalizeUmlauts(buchtyp ?? "").toLowerCase().trim();
    if (!key) {
      return 0;
    }
    const synonyms = MH.typeSynonyms[key];
    for (const word of synonyms ? synonyms.words : [key]) {
      if (MH.wordInTokens(word, optionTokens)) {
        return 40;
      }
    }
    if (synonyms && synonyms.symbol.test(optionText)) {
      return 40;
    }
    // A mixed volume may contain any record type - weak match, enough to be
    // listed as similar but never enough to auto-select.
    if (optionTokens.includes("mischband")) {
      return 20;
    }
    return 0;
  },

  // The signatur must appear as a delimited token, not as a substring of a
  // longer number - signatur "9" must match "Buch 9" but not the trailing
  // digit of "1859". (Its +60 weight would otherwise auto-select wrong
  // books off such coincidences.)
  signaturMatches(optionText, signatur) {
    if (!signatur) {
      return false;
    }
    const escaped = signatur.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
    return new RegExp(`(^|[^0-9a-zä-ü])${escaped}([^0-9a-zä-ü]|$)`, "i")
      .test(optionText);
  },

  // Rank an Ancestry source title against a Matricula citation.
  // Domain rules: no parish, no candidate (hard gate, typos tolerated);
  // parish + Signatur is near-certain - reported as `certain`, the explicit
  // auto-select criterion (not encoded in score arithmetic); record type
  // counts with synonyms/symbols; a city prefix and years only improve the
  // ranking, never certainty.
  evaluate(optionText, citation) {
    const optionTokens = MH.tokenize(optionText);
    const parish = MH.parishMatch(optionTokens, citation.pfarrei);
    if (!parish.matched) {
      return { score: 0, certain: false };
    }

    let score = 40; // parish
    if (parish.cityHint) {
      score += 10;
    }

    const signatur = MH.signaturMatches(optionText, citation.signatur);
    if (signatur) {
      score += 60;
    }

    score += MH.typeMatchScore(optionText, optionTokens, citation.buchtyp);

    const from = MH.yearOf(citation.datumVon);
    const to = MH.yearOf(citation.datumBis);
    if (from !== null && to !== null) {
      for (const m of optionText.matchAll(/\d{4}/g)) {
        const y = parseInt(m[0], 10);
        if (y >= from && y <= to) {
          score += 10;
          break;
        }
      }
    }
    return { score, certain: signatur };
  },

  score(optionText, citation) {
    return MH.evaluate(optionText, citation).score;
  },

  // Book-level source title, e.g. "Titting Taufen 1599-1625" (port of
  // MatriculaInfo.CitationTitle) - no scan/page reference, matching the
  // genealogy convention that the "source" is the book and the page is a
  // citation detail.
  citationTitle(c) {
    return `${c.pfarrei} ${c.buchtyp} ${MH.yearOf(c.datumVon) ?? ""}-${MH.yearOf(c.datumBis) ?? ""}`;
  },

  // "285 (Scan 7)" or "Scan 7" if the priest-written page number wasn't
  // parseable (port of MatriculaInfo.PageDescription).
  pageDescription(c) {
    return c.page !== null && c.page !== undefined
      ? `${c.page} (Scan ${c.scan})`
      : `Scan ${c.scan}`;
  },

  // One-line label for the saved-entries list (port of SavedEntry's
  // DisplayText: "{Pfarrei} {Buchtyp} {YearFrom}-{YearTo} {ScanLabel}").
  displayText(c) {
    return `${c.pfarrei} ${c.buchtyp} ${MH.yearOf(c.datumVon) ?? ""}-${MH.yearOf(c.datumBis) ?? ""} ${c.scanLabel ?? ""}`.trim();
  },

  // Multi-line detail block (port of MainViewModel.Format).
  format(c) {
    return [
      `Bistum ${c.bistum}`,
      `Pfarrei ${c.pfarrei}`,
      `Buch ${c.buchtyp} ${c.datumVon} - ${c.datumBis}`,
      `Signatur: ${c.signatur}`,
      `Seite ${MH.pageDescription(c)}`,
      `URL: ${c.url}`,
    ].join("\n");
  },
};
