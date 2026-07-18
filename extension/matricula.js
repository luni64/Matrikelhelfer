// Content script for data.matricula-online.eu: extracts the citation
// metadata for the scan page being viewed and keeps storage.local's
// `currentCitation` up to date. DOM port of the WPF app's
// Services/MatriculaExtractor - but reading the live document directly, so
// no second HTTP fetch and no path-keyed cache are needed.
"use strict";

(() => {
  function tableValue(label) {
    // <tr><th>Buchtyp</th><td>...</td></tr> - lookup by the site's own
    // German field labels (same as the XPath th[text()=...] approach).
    for (const th of document.querySelectorAll("tr > th")) {
      if (th.textContent.trim() === label) {
        const td = th.nextElementSibling;
        if (td && td.tagName === "TD") {
          return td.textContent.trim();
        }
      }
    }
    return null;
  }

  // The breadcrumb links carry names for their own path level, so look up
  // the parent path (levelsUp segments stripped) by href instead of by a
  // fixed breadcrumb position - hierarchy depth isn't assumed fixed.
  function breadcrumb(currentPath, levelsUp) {
    const segments = currentPath.replace(/\/+$/, "").split("/");
    if (segments.length <= levelsUp) {
      return null;
    }
    const targetPath = segments.slice(0, segments.length - levelsUp).join("/") + "/";
    for (const a of document.querySelectorAll("ul.breadcrumb li.breadcrumb-item a")) {
      if (a.getAttribute("href") === targetPath) {
        return a.textContent.trim();
      }
    }
    return null;
  }

  // The viewer widget is initialized with a JS object literal (NOT strict
  // JSON - some values are bare identifiers), so only the inner "labels"
  // array is extracted and parsed. It holds the priest-written page label
  // per scan, index-aligned with the "pg" query parameter (labels[pg-1]).
  function extractLabels() {
    for (const script of document.querySelectorAll("script")) {
      const m = /"labels"\s*:\s*(\[[^\]]*\])/s.exec(script.textContent);
      if (m) {
        try {
          return JSON.parse(m[1]);
        } catch {
          return null;
        }
      }
    }
    return null;
  }

  function extract() {
    const url = new URL(location.href);
    const pg = parseInt(url.searchParams.get("pg") ?? "1", 10);

    const buchtyp = tableValue("Buchtyp");
    const signatur = tableValue("Signatur");
    if (!buchtyp && !signatur) {
      // Not a scan page (start page, parish overview, search, ...) - leave
      // whatever citation was last published untouched, mirroring the WPF
      // app's "sticky" behavior when switching to a non-Matricula page.
      return;
    }

    const labels = extractLabels();
    const scanLabel = labels && pg >= 1 && pg <= labels.length ? labels[pg - 1] : "";
    const pageMatch = /(\d+)\s*$/.exec(scanLabel ?? "");

    const citation = {
      bistum: breadcrumb(url.pathname, 2) ?? "",
      pfarrei: breadcrumb(url.pathname, 1) ?? "",
      buchtyp: buchtyp ?? "",
      datumVon: tableValue("Datum von") ?? "",
      datumBis: tableValue("Datum bis") ?? "",
      signatur: signatur ?? "",
      scan: pg,
      page: pageMatch ? pageMatch[1] : null,
      scanLabel: scanLabel ?? "",
      url: location.href,
      updatedAt: Date.now(),
    };
    MH.api.storage.local.set({ currentCitation: citation });
  }

  // Matricula's page-turn control updates the ?pg= parameter without a full
  // page load, so a plain load-time extraction would go stale. Poll the URL
  // (cheap) and re-extract on any change.
  let lastHref = null;
  setInterval(() => {
    if (location.href !== lastHref) {
      lastHref = location.href;
      extract();
    }
  }, 500);
})();
