using System;
using System.Collections.Generic;
using System.Linq;
using GrampsBridge;

namespace Matrikelhelfer.Services;

// The local person/family graph (discussion 2026-08-19): loaded and
// newly created persons are the SAME kind of node, so navigation,
// slot creation and display never special-case virtual persons. The
// family object sits between persons, mirroring Gramps's own model
// (child_of_family, family events, multiple marriages).
//
// Persistence readiness ("save and continue later", not implemented
// yet): node ids are stable (Gramps handle or "new:<guid>"), edges are
// resolvable by id, and nodes hold plain values only — the persistent
// form will be flat records with id references (like library.json),
// storing the VIRTUAL subgraph as data and real persons as handle
// references to be revalidated against Gramps on resume.

/// <summary>One person node: a real Gramps person (id = handle, data
/// upserted from the bridge) or a virtual one (id = "new:&lt;guid&gt;",
/// created locally, persisted on upload).</summary>
sealed class TreePerson(string id)
{
    public string Id { get; internal set; } = id;

    public bool IsVirtual => Id.StartsWith("new:", StringComparison.Ordinal);

    /// <summary>The CreatePerson change-entry id of a virtual node.</summary>
    public string EntryId => IsVirtual ? Id[4..] : Id;

    public string? GrampsId { get; set; }
    public string ServerName { get; set; } = "";       // real persons

    /// <summary>The STRUCTURED surname from the bridge's person detail
    /// (Gramps models names structured; only the display string needs
    /// parsing). Empty for brief-only nodes — callers fall back to
    /// parsing ServerName then.</summary>
    public string ServerSurname { get; set; } = "";

    public string Given { get; set; } = "";            // virtual persons
    public string Surname { get; set; } = "";
    public string Gender { get; set; } = "U";
    public LifeEvent? Birth { get; set; }
    public LifeEvent? Death { get; set; }

    /// <summary>Full detail (events, parents, families) fetched — briefs
    /// only fill the box; the center needs this.</summary>
    public bool DetailLoaded { get; set; }

    public TreeFamily? ParentFamily { get; set; }
    public List<TreeFamily> Families { get; } = [];
    public List<PersonEvent> Events { get; set; } = [];

    /// <summary>Findings adopted into this person's working set (tray
    /// card dragged onto the person) — session-local UI staging: only
    /// the ASSIGNMENTS (change entries) are worth keeping, an adopted
    /// but never-assigned card writes nothing.</summary>
    public List<Guid> AdoptedFindings { get; } = [];

    public string DisplayName => IsVirtual
        ? string.Join(" ", new[] { Given, Surname }.Where(p => p.Length > 0))
        : ServerName;
}

/// <summary>One family node: partners + children (+ family events via
/// the person detail's event list). Virtual families materialize as a
/// side effect of the first create_person capture that references them
/// (spouse_of / parent_of / child_of_person return created.family).</summary>
sealed class TreeFamily(string id)
{
    public string Id { get; internal set; } = id;

    public bool IsVirtual => Id.StartsWith("new:", StringComparison.Ordinal);

    public TreePerson? Father { get; set; }
    public TreePerson? Mother { get; set; }
    public List<TreePerson> Children { get; } = [];

    public TreePerson? PartnerOf(TreePerson person) =>
        Father == person ? Mother : Mother == person ? Father : null;
}

/// <summary>Identity map + upsert logic. Server fetches update nodes in
/// place (spec 7.3: refreshes never move the view); virtual creation
/// uses the same nodes. All lookups by id.</summary>
sealed class TreeGraph
{
    private readonly Dictionary<string, TreePerson> _persons = [];
    private readonly Dictionary<string, TreeFamily> _families = [];

    public IEnumerable<TreeFamily> AllFamilies => _families.Values;

    public TreePerson? Person(string? id) =>
        id is not null && _persons.TryGetValue(id, out var person) ? person : null;

    public TreeFamily? Family(string? id) =>
        id is not null && _families.TryGetValue(id, out var family) ? family : null;

    public TreePerson GetOrAddPerson(string id) =>
        _persons.TryGetValue(id, out var person)
            ? person : _persons[id] = new TreePerson(id);

    public TreeFamily GetOrAddFamily(string id) =>
        _families.TryGetValue(id, out var family)
            ? family : _families[id] = new TreeFamily(id);

    public void Clear()
    {
        _persons.Clear();
        _families.Clear();
    }

    // ---- upserts from server DTOs ------------------------------------

    public TreePerson UpsertBrief(PersonBrief brief)
    {
        var person = GetOrAddPerson(brief.Handle);
        person.ServerName = brief.PrimaryName ?? "(ohne Name)";
        person.GrampsId = brief.GrampsId;
        if (brief.Gender is { Length: > 0 } gender)
            person.Gender = gender;
        person.Birth = brief.Birth;
        person.Death = brief.Death;
        return person;
    }

    public TreePerson UpsertDetail(PersonDetail detail)
    {
        var person = GetOrAddPerson(detail.Handle);
        person.ServerName = detail.PrimaryName;
        person.ServerSurname = detail.Names
            .FirstOrDefault(n => n.Primary)?.Surname ?? "";
        person.GrampsId = detail.GrampsId;
        person.Gender = detail.Gender;
        person.Birth = detail.Birth;
        person.Death = detail.Death;
        person.Events = detail.Events;
        person.DetailLoaded = true;

        if (detail.ParentFamilyHandle is { } parentFamilyHandle)
        {
            var family = GetOrAddFamily(parentFamilyHandle);
            person.ParentFamily = family;
            if (!family.Children.Contains(person))
                family.Children.Add(person);
            foreach (var parentBrief in detail.Parents)
            {
                var parent = UpsertBrief(parentBrief);
                PlacePartner(family, parent);
                if (!parent.Families.Contains(family))
                    parent.Families.Add(family);
            }
        }

        foreach (var info in detail.Families)
        {
            var family = GetOrAddFamily(info.Handle);
            if (!person.Families.Contains(family))
                person.Families.Add(family);
            PlacePartner(family, person);
            if (info.Spouse is { } spouseBrief)
            {
                var spouse = UpsertBrief(spouseBrief);
                PlacePartner(family, spouse);
                if (!spouse.Families.Contains(family))
                    spouse.Families.Add(family);
            }
            // children: server truth wins, locally created ones survive
            var virtuals = family.Children.Where(c => c.IsVirtual).ToList();
            family.Children.Clear();
            foreach (var childBrief in info.Children)
            {
                var child = UpsertBrief(childBrief);
                family.Children.Add(child);
                child.ParentFamily ??= family;
            }
            family.Children.AddRange(virtuals);
        }
        return person;
    }

    /// <summary>Father/mother slot by gender; unknown takes a free slot.
    /// No-op when already placed; an occupied preferred slot falls back
    /// to the other one.</summary>
    public static void PlacePartner(TreeFamily family, TreePerson person)
    {
        if (family.Father == person || family.Mother == person)
            return;
        var preferFather = person.Gender switch
        {
            "M" => true,
            "F" => false,
            _ => family.Father is null,
        };
        if (preferFather)
        {
            if (family.Father is null)
                family.Father = person;
            else
                family.Mother = person;
        }
        else
        {
            if (family.Mother is null)
                family.Mother = person;
            else
                family.Father = person;
        }
    }

    // ---- virtual creation / removal ----------------------------------

    public TreePerson AddVirtualPerson(string entryId, string given,
                                       string surname, string gender)
    {
        var person = GetOrAddPerson("new:" + entryId);
        person.Given = given;
        person.Surname = surname;
        person.Gender = gender;
        return person;
    }

    public TreeFamily AddVirtualFamily() =>
        GetOrAddFamily("new:" + Guid.NewGuid().ToString("N"));

    /// <summary>Unlink and remove a virtual person; prune virtual
    /// families that lost their reason to exist. Returns further
    /// virtual persons that became unreachable (their change entries
    /// must go too — the caller owns the change list).</summary>
    public List<TreePerson> RemoveVirtualPerson(TreePerson person)
    {
        Unlink(person);
        _persons.Remove(person.Id);
        return Prune();
    }

    private void Unlink(TreePerson person)
    {
        person.ParentFamily?.Children.Remove(person);
        person.ParentFamily = null;
        foreach (var family in person.Families)
        {
            if (family.Father == person)
                family.Father = null;
            if (family.Mother == person)
                family.Mother = null;
        }
        person.Families.Clear();
    }

    private List<TreePerson> Prune()
    {
        var orphans = new List<TreePerson>();
        bool changed;
        do
        {
            changed = false;
            // a virtual family needs a pending member (else it cannot
            // materialize) and at least one partner (else no capture
            // can create it)
            foreach (var family in _families.Values
                         .Where(f => f.IsVirtual).ToList())
            {
                var hasVirtualMember = family.Father?.IsVirtual == true
                    || family.Mother?.IsVirtual == true
                    || family.Children.Any(c => c.IsVirtual);
                var hasPartner = family.Father is not null
                    || family.Mother is not null;
                if (hasVirtualMember && hasPartner)
                    continue;
                foreach (var member in new[] { family.Father, family.Mother })
                    member?.Families.Remove(family);
                foreach (var child in family.Children)
                    if (child.ParentFamily == family)
                        child.ParentFamily = null;
                _families.Remove(family.Id);
                changed = true;
            }
            // a virtual person without any link can never be uploaded
            foreach (var person in _persons.Values
                         .Where(p => p.IsVirtual && p.ParentFamily is null
                                     && p.Families.Count == 0).ToList())
            {
                _persons.Remove(person.Id);
                orphans.Add(person);
                changed = true;
            }
        } while (changed);
        return orphans;
    }

    // ---- upload: temp id -> real handle, node identity kept ----------

    public void ReplacePersonId(TreePerson person, string handle)
    {
        _persons.Remove(person.Id);
        person.Id = handle;
        _persons[handle] = person;
    }

    public void ReplaceFamilyId(TreeFamily family, string handle)
    {
        _families.Remove(family.Id);
        family.Id = handle;
        _families[handle] = family;
    }
}
