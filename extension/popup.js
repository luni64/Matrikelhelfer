// Popup/sidebar page: shows the current citation (live via
// storage.onChanged) and manages the saved-citations list. Port of the WPF
// MainViewModel/MainWindow, with storage.local replacing the in-memory
// ObservableCollection - saved entries survive browser restarts.
"use strict";

const currentEl = document.getElementById("current");
const saveBtn = document.getElementById("save");
const copyBtn = document.getElementById("copy");
const savedList = document.getElementById("saved");
const detailEl = document.getElementById("detail");

let current = null;

async function refresh() {
  const { currentCitation, savedCitations } =
    await MH.api.storage.local.get(["currentCitation", "savedCitations"]);

  current = currentCitation ?? null;
  if (current) {
    currentEl.textContent = MH.format(current);
  }
  saveBtn.disabled = !current;
  copyBtn.disabled = !current;

  renderSaved(savedCitations ?? []);
}

function renderSaved(saved) {
  savedList.replaceChildren();
  saved.forEach((citation, index) => {
    const li = document.createElement("li");

    const label = document.createElement("span");
    label.className = "label";
    label.textContent = MH.displayText(citation);
    label.title = "Details anzeigen";
    label.addEventListener("click", () => {
      detailEl.textContent = MH.format(citation);
      detailEl.hidden = false;
    });

    const del = document.createElement("button");
    del.className = "delete";
    del.textContent = "×";
    del.title = "Löschen";
    del.addEventListener("click", async () => {
      saved.splice(index, 1);
      await MH.api.storage.local.set({ savedCitations: saved });
    });

    li.append(label, del);
    savedList.append(li);
  });
}

saveBtn.addEventListener("click", async () => {
  if (!current) {
    return;
  }
  const { savedCitations } = await MH.api.storage.local.get("savedCitations");
  const saved = savedCitations ?? [];
  saved.push(current);
  await MH.api.storage.local.set({ savedCitations: saved });
});

copyBtn.addEventListener("click", () => {
  if (current) {
    navigator.clipboard.writeText(MH.format(current));
  }
});

MH.api.storage.onChanged.addListener(refresh);
refresh();
