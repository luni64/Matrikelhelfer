# Matrikelhelfer – Benutzerhandbuch

> Dieses Handbuch ist in Arbeit – die App befindet sich noch in der Entwicklung. Screenshots folgen.

## 1. Einführung

Matrikelhelfer unterstützt Sie bei der Ahnenforschung in Online-Kirchenbüchern. Während Sie im Browser einen Kirchenbuch-Scan betrachten, holt die App auf Knopfdruck alle Angaben, die Sie für eine saubere Quellenangabe brauchen:

- **Land, Bistum und Pfarrei**
- **Buchtyp und Zeitraum** (z. B. Taufen 1670–1736)
- **Signatur** (bei Archiven mit geteilter Signatur auch getrennt nach Pfarrei-Bestand und Buch)
- **Scan-Nummer und Scan-ID**
- **Links** auf die Seite, das Buch und die Bilddatei

Aus diesen Angaben erzeugt die App fertig formatierte, mit einem Klick kopierbare **Quellen- und Zitatangaben** – in frei definierbaren Formaten, passend zur Arbeitsweise von Genealogie-Programmen (eine *Quelle* pro Kirchenbuch, dazu je Fundstelle ein *Zitat*).

Interessante Funde können Sie außerdem mit Name und Kommentar versehen und dauerhaft **speichern**. Das ist besonders praktisch, wenn Sie beim Blättern auf etwas stoßen, für das gerade keine Zeit ist: Fund speichern, kurz benennen und kommentieren – und später über die Liste der gespeicherten Einträge gezielt wieder aufgreifen. Name und Kommentar helfen dabei, den Fund in der Liste wiederzufinden; ein Doppelklick auf den Eintrag öffnet die gespeicherte Seite wieder im Browser. Die gesammelten Einträge lassen sich zudem als CSV-Datei für Ihr eigenes Archiv exportieren.

**Unterstützte Anbieter:**

- [Matricula Online](https://data.matricula-online.eu) – Kirchenbücher aus Deutschland, Österreich, Luxemburg u. a.
- **DFG-Viewer**-basierte Archive (`tx_dlf`), derzeit das [Digitale Archiv des Erzbistums München und Freising](https://digitales-archiv.erzbistum-muenchen.de)

Die App erkennt den Anbieter automatisch an der Adresse der gerade angezeigten Browser-Seite.

---

## 2. Oberfläche im Überblick

Das Fenster besteht von oben nach unten aus:

1. **Werkzeugleiste** – links, in der Reihenfolge des Arbeitsablaufs: **Verbinden** (Steckersymbol) und **Lesen** (Buchsymbol); rechts **Speichern** (Diskettensymbol) und die Umschaltfläche für die **Liste der gespeicherten Einträge**. Das Zahnrad für die **Einstellungen** sitzt in der Titelleiste.
2. **Notizen** – die Felder **Name** und **Kommentar**, mit denen Sie einen Fund beschriften, bevor Sie ihn speichern.
3. **Quelle** – Bistum, Pfarrei, Buchtyp und Signatur des Kirchenbuchs sowie die fertige **Quellenangabe** (Auswahlliste mit Ihren Quellenformaten).
4. **Zitat** – das von Ihnen einzutragende Feld **Seite**, die **Scan-ID** sowie die fertige **Zitatangabe** (Auswahlliste mit Ihren Zitatformaten).
5. **Links** – die Adressen der Kirchenbuch-Seite und der Bilddatei.
6. **Statusleiste** – eine Zeile am unteren Rand für Hinweise und Fehlermeldungen (lange Meldungen werden gekürzt; halten Sie den Mauszeiger auf die Meldung, um den vollständigen Text zu sehen).

Jedes Anzeigefeld hat rechts ein **Kopier-Symbol**, das den Feldinhalt in die Zwischenablage legt.

<!-- ![Hauptfenster – Gesamtansicht](Pictures/02_hauptfenster.png) -->
<!-- Zeigt: Hauptfenster mit gelesener Matricula-Seite, alle Abschnitte gefüllt -->

---

## 3. Schnellstart

1. Öffnen Sie im Browser eine Kirchenbuch-Scan-Seite (Matricula Online oder DFG-Viewer).
2. Klicken Sie in Matrikelhelfer auf das **Steckersymbol** und wählen Sie Ihren Browser aus.
3. Klicken Sie auf **Lesen** (Buchsymbol) – die Felder füllen sich mit den Angaben des Kirchenbuchs.
4. Tragen Sie im Feld **Seite** die handschriftliche Seitennummer ein (siehe [Kapitel 6](#6-seite-scan-id-und-scan-nr)).
5. Kopieren Sie Quellen- und Zitatangabe mit den Kopier-Symbolen in Ihr Genealogie-Programm.
6. Optional: Name und Kommentar eintragen und den Fund mit **Speichern** (Diskette) dauerhaft ablegen.

---

## 4. Mit dem Browser verbinden

- Klicken Sie auf das **Steckersymbol**. Ein Auswahlfenster zeigt alle laufenden, unterstützten Browser (Chrome, Edge, Brave, Opera, Vivaldi, Firefox).
- Nach dem Verbinden wird das Steckersymbol **grün**; halten Sie den Mauszeiger darauf, sehen Sie, mit welchem Browser die App verbunden ist.
- Ein erneuter Klick auf das Steckersymbol trennt die Verbindung.

Die Verbindung ist rein lesend und **auf Abruf**: Die App liest die Adresszeile des Browsers nur in dem Moment, in dem Sie auf **Lesen** klicken – sie beobachtet Ihren Browser nicht dauerhaft.

Wird der verbundene Browser geschlossen, erkennt die App das beim nächsten **Lesen** und zeigt es in der Statusleiste an; verbinden Sie sich dann einfach neu.

<!-- ![Browser-Auswahl](Pictures/04_browserauswahl.png) -->
<!-- Zeigt: Auswahlfenster mit laufenden Browsern samt Icons -->

---

## 5. Kirchenbuchseite lesen

Ein Klick auf **Lesen** (Buchsymbol):

1. liest die Adresse des aktiven Browser-Tabs,
2. erkennt daran den Anbieter (Matricula oder DFG-Viewer),
3. lädt die Angaben des Kirchenbuchs und füllt alle Felder.

Beim Blättern im Scan (nächste/vorherige Seite im Viewer) genügt ein erneuter Klick auf **Lesen** – die App erkennt, dass es sich um dasselbe Buch handelt, und aktualisiert nur die Scan-Angaben, ohne die Seite neu aus dem Internet zu laden. Pro Buch und Sitzung wird nur einmal geladen; der Datenverkehr bleibt minimal.

**Wichtig:** Jeder Klick auf **Lesen** beginnt einen neuen Fund – alle Felder (auch Name, Kommentar und Seite) werden zuvor geleert. Enthalten die Notizfelder Eingaben, die noch nicht gespeichert wurden, fragt die App vorher nach (siehe [Kapitel 7](#7-funde-speichern)).

Zeigt der Browser keine lesbare Kirchenbuch-Seite (z. B. eine Pfarrei-Übersichtsseite oder eine beliebige andere Website), meldet die Statusleiste „Keine unterstützte Kirchenbuch-Seite“ und die Felder bleiben leer.

---

## 6. Seite, Scan-ID und Scan-Nr

Diese drei Begriffe tauchen in der App und in den Formaten auf und bezeichnen Verschiedenes:

- **Scan-Nr** – die laufende Nummer des Scans im Viewer (aus der Adresszeile). Sie zählt alle Aufnahmen durch, inklusive Einband und Leerseiten.
- **Scan-ID** – die Beschriftung, die der Anbieter dem Scan gibt (bei Matricula z. B. „Pollenfeld 01. 007“). Pflegt ein Archiv keine Beschriftungen (z. B. das Erzbistum-München-Archiv), zeigt das Feld die Scan-Nummer.
- **Seite** – die **handschriftliche Seitennummer** im Kirchenbuch selbst.

Das Feld **Seite** ist bewusst das einzige, das Sie selbst ausfüllen: Die in Scan-Beschriftungen enthaltene Nummer stimmt selten mit der tatsächlichen handschriftlichen Seitenzahl überein, deshalb rät die App grundsätzlich nicht. Solange das Feld leer ist, zeigt es einen **roten Rand** als Erinnerung, und der Platzhalter `{Seite}` bleibt in den Angaben leer – eine Zitatangabe soll niemals eine Scan-Nummer als Seitenzahl ausgeben.

---

## 7. Funde speichern

Mit **Speichern** (Diskettensymbol) legen Sie den aktuell angezeigten Fund dauerhaft ab – zusammen mit Ihren Notizen:

- **Name** – die gefundene Person, z. B. „Anna Maier, *1846“.
- **Kommentar** – Freitext: Warum ist diese Seite interessant? Mehrzeilige Eingaben sind möglich.

Hinweise:

- **Speichern** ist ausgegraut, solange der angezeigte Fund samt Notizen exakt einem bereits gespeicherten Eintrag entspricht – das verhindert versehentliche Doppel-Speicherungen. Dieselbe Seite mit anderem Namen oder Kommentar lässt sich jederzeit erneut speichern (mehrere Funde pro Seite sind normal).
- Enthalten Name, Kommentar oder Seite noch nicht gespeicherte Eingaben, warnt die App, bevor diese durch **Lesen** oder die Auswahl eines gespeicherten Eintrags verloren gehen.
- Die Einträge werden in `%APPDATA%\Matrikelhelfer\entries.json` gespeichert – eine gut lesbare Textdatei, die Sie einfach sichern können. Gespeichert werden nur die Rohdaten; die Quellen- und Zitatangaben werden bei der Anzeige stets mit Ihren **aktuellen** Formaten neu erzeugt.

---

## 8. Gespeicherte Einträge

Die Umschaltfläche rechts in der Werkzeugleiste öffnet die **Liste der gespeicherten Einträge** in einem eigenen Bereich rechts neben den Feldern (das Fenster wird dazu breiter; die Trennlinie lässt sich mit der Maus verschieben):

- Spalten: **Name**, **Buch**, **Seite**, **Gespeichert** – per Klick auf die Spaltenköpfe sortierbar, neueste zuerst.
- Halten Sie den Mauszeiger auf eine Zeile, wird der **Kommentar** des Eintrags eingeblendet.
- **Einfacher Klick:** zeigt den Eintrag wieder in den Hauptfeldern an.
- **Doppelklick** (oder Kontextmenü „Im Browser öffnen“): steuert zusätzlich den verbundenen Browser zurück zur gespeicherten Scan-Seite.
- **Entf** (oder Kontextmenü „Eintrag löschen“): löscht den Eintrag endgültig.

Bearbeiten Sie bei einem wieder angezeigten Eintrag die Notizen, können Sie ihn mit **Speichern** als weiteren Fund derselben Seite ablegen.

<!-- ![Gespeicherte Einträge](Pictures/08_gespeicherte_eintraege.png) -->
<!-- Zeigt: geöffnetes Panel mit mehreren Einträgen, ein Kommentar-Tooltip sichtbar -->

### 8.1 CSV-Export

Das **Export-Symbol** über der Liste schreibt alle gespeicherten Einträge in eine CSV-Datei – gedacht für Ihr eigenes Langzeitarchiv, z. B. um Jahre später nachzusehen, ob Sie ein Buch schon einmal durchgearbeitet haben.

- Enthält alle Einzelfelder (inklusive der getrennten Signaturen und aller Links) **plus** die fertige Quellen- und Zitatangabe in den gerade gewählten Formaten.
- Das Format ist auf deutsches Excel abgestimmt (Semikolon-Trennung, UTF-8 mit BOM): Ein Doppelklick auf die Datei öffnet sie korrekt mit allen Umlauten; mehrzeilige Kommentare bleiben erhalten.

---

## 9. Quellen- und Zitatangabe

Genealogie-Programme unterscheiden zwischen der **Quelle** (dem Kirchenbuch) und dem **Zitat** (der konkreten Fundstelle auf einer Seite). Matrikelhelfer bildet das mit zwei getrennten Feldern ab:

- **Quellenangabe** – identifiziert das Buch, z. B. „Erzbistum München und Freising, Waging am See-St. Martin, Taufen 1846–1885, Signatur CB481, M7658.“
- **Zitatangabe** – die Fundstelle, z. B. „S. 12, ID: 007“.

Beide Felder sind Auswahllisten: Aufgeklappt zeigen sie alle Ihre Formate, jeweils schon mit den Daten des aktuellen Fundes ausgefüllt – Sie wählen einfach die Variante, die Ihnen gefällt. Die Auswahl bleibt auch nach einem Neustart erhalten. Das Kopier-Symbol daneben legt den angezeigten Text in die Zwischenablage.

---

## 10. Formate bearbeiten

Das **Zahnrad in der Titelleiste** öffnet den Formateditor. Links wählen Sie zunächst, welche Liste Sie bearbeiten möchten (**Quellenformate** oder **Zitatformate**), und darunter das einzelne Format; mit **+** und **Papierkorb** legen Sie Formate an bzw. löschen sie (das letzte Format einer Liste kann nicht gelöscht werden). Änderungen gelten erst nach **OK**; **Abbrechen** verwirft sie.

Rechts bearbeiten Sie das gewählte Format:

- **Name** – der Anzeigename in der Auswahlliste.
- **Formatvorlage** – der Text der Angabe mit `{Platzhaltern}`, die automatisch durch die Werte des Fundes ersetzt werden.
- **Platzhalter-Pillen** – ein Klick fügt den Platzhalter an der Schreibmarke ein. Halten Sie den Mauszeiger auf eine Pille, erscheint eine kurze Erklärung mit Beispielwert. Die vollständige Referenz steht im [Anhang](#anhang-platzhalter-referenz).
- **Datumsformat** – bestimmt, wie die Datums-Platzhalter `{Von}`, `{Bis}` und `{AccessDate}` ausgegeben werden (siehe [10.1](#101-datumsformate)).
- **Vorschau** – zeigt das Format live mit Beispielwerten.

Die Formate werden in `%APPDATA%\Matrikelhelfer\formats.json` gespeichert.

<!-- ![Formateditor](Pictures/10_formateditor.png) -->
<!-- Zeigt: Einstellungsdialog mit Formatliste links, Editor mit Pillen-Zeilen, Datumsformat-Dropdown und Vorschau rechts -->

### 10.1 Datumsformate

Jedes Format hat sein eigenes Datumsformat – so kann ein englisches Zitierformat englische Datumsangaben verwenden, während Ihre deutschen Formate deutsch bleiben:

| Auswahl | Beispiel |
|---|---|
| Original (wie vom Anbieter) | „1. Januar 1670“ bzw. „01.01.1846“ – unverändert |
| Numerisch | 22.03.1845 |
| Deutsch lang | 22. März 1845 |
| GEDCOM deutsch | 22 MÄR 1845 |
| ISO | 1845-03-22 |
| Englisch lang | 22 March 1845 |
| Englisch (US) | Mar 22, 1845 |
| GEDCOM englisch | 22 MAR 1845 |

Kann ein Datum nicht als solches erkannt werden (z. B. „um 1650“), wird es unverändert übernommen – die App erfindet keine Datumsangaben.

---

## 11. Links und Bild speichern

Im Abschnitt **Links**:

- **Link auf die Kirchenbuch-Seite** – die Adresse des aktuellen Scans. Das Pfeil-Symbol öffnet sie im verbundenen Browser.
- **Link auf die Bilddatei** – die direkte Adresse des Scan-Bildes. Das Download-Symbol lädt das Bild herunter und speichert es unter einem sprechenden Dateinamen, der Bistum, Pfarrei, Buch, Signatur und Scan-ID enthält – so bleibt auch ohne die App erkennbar, was die Datei zeigt.

Für Formate stehen zusätzlich `{BookUrl}` (Link auf das Buch, ohne Seitenangabe) und `{PageUrl}` (Link auf den aktuellen Scan) zur Verfügung.

---

## 12. Tipps für die Praxis

- Tragen Sie die **Seite** direkt nach dem Lesen ein, solange Sie den Scan noch vor Augen haben – das rote Feld erinnert Sie daran.
- Nutzen Sie **kurze, klare Namensformen** im Notizfeld (z. B. „Anna Maier, *1846“), damit die Einträge in der Liste und im CSV-Export gut lesbar bleiben.
- Mehrere Funde auf derselben Seite? Einfach die Notizen ändern und erneut **Speichern** – jeder Fund wird ein eigener Eintrag.
- Exportieren Sie die Einträge gelegentlich als **CSV** und legen Sie die Datei zu Ihren Forschungsunterlagen; sichern Sie zusätzlich `entries.json` mit Ihrer normalen Datensicherung.
- Der **Doppelklick** auf einen gespeicherten Eintrag ist der schnellste Weg, eine früher gefundene Stelle wieder im Browser zu öffnen.

---

## 13. Häufige Fragen (FAQ)

**Die Statusleiste meldet „Keine unterstützte Kirchenbuch-Seite“ – warum?**
Der aktive Browser-Tab zeigt keine lesbare Scan-Seite. Übersichtsseiten (Pfarrei, Bistum) enthalten keine Buchdaten – öffnen Sie ein konkretes Buch im Viewer und klicken Sie erneut auf **Lesen**.

**„Browser-Adressleiste konnte nicht gelesen werden“?**
Der verbundene Browser wurde möglicherweise geschlossen oder neu gestartet. Verbinden Sie sich über das Steckersymbol neu.

**Warum ist das Feld Seite nach jedem Lesen leer?**
Absichtlich – siehe [Kapitel 6](#6-seite-scan-id-und-scan-nr): Die App übernimmt keine Scan-Nummern als Seitenzahlen; nur Ihre eigene Eingabe zählt.

**Warum ist Speichern ausgegraut?**
Entweder wurde noch keine Seite gelesen, oder der angezeigte Fund samt Notizen ist bereits exakt so gespeichert. Ändern Sie z. B. den Kommentar, wird Speichern wieder aktiv.

**Werden meine Daten irgendwohin übertragen?**
Nein. Die App liest nur die Adresszeile des von Ihnen gewählten Browsers und lädt die Buchdaten direkt beim jeweiligen Kirchenbuch-Anbieter. Alle gespeicherten Daten liegen lokal in `%APPDATA%\Matrikelhelfer`.

---

## 14. Installation

Noch nicht als Download verfügbar – die App ist in Entwicklung und wird bisher aus dem Quellcode gebaut (siehe [Repository](https://github.com/luni64/Matrikelhelfer)). Geplant sind wie bei AutoNumber ein ZIP-Archiv (portabel) und ein Installer auf der GitHub-Releases-Seite.

---

## Anhang: Platzhalter-Referenz

| Platzhalter | Bedeutung | Beispiel |
|---|---|---|
| `{Land}` | Land | Deutschland |
| `{Bistum}` | Bistum bzw. Archiv | Erzbistum München und Freising |
| `{Pfarrei}` | Pfarrei | Waging am See-St. Martin |
| `{SignaturPfarrei}` | Signatur des Pfarrei-Bestands (nicht bei Matricula) | CB481 |
| `{SignaturBuch}` | Signatur des einzelnen Buchs | M7658 |
| `{Signatur}` | Vollständige Signatur | CB481, M7658 |
| `{Buchtyp}` | Art des Kirchenbuchs | Taufen |
| `{Von}` / `{Bis}` | Beginn/Ende des Buchzeitraums, im gewählten Datumsformat | 1. Januar 1670 |
| `{JahrVon}` / `{JahrBis}` | Nur das Jahr des Buchbeginns/-endes | 1670 |
| `{Seite}` | Handschriftliche Seitennummer – nur Ihre Eingabe, sonst leer | 12 |
| `{Scan-ID}` | Scan-Beschriftung des Anbieters | Pollenfeld 01. 007 |
| `{Scan-Nr}` | Scan-Nummer im Viewer | 8 |
| `{BookUrl}` | Link auf das Buch (ohne Seitenangabe) | … |
| `{PageUrl}` | Link auf die aktuelle Scan-Seite | … |
| `{ImageUrl}` | Direktlink auf die Bilddatei des Scans | … |
| `{AccessDate}` | Heutiges Datum (Zugriffsdatum), im gewählten Datumsformat | 18. Juli 2026 |

---

*Dieses Handbuch beschreibt Matrikelhelfer V0.1.0 (in Entwicklung). Screenshots und weitere Beispiele folgen.*
