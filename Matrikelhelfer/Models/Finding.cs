using System;

namespace Matrikelhelfer.Models;

// One find: a person spotted on a scan, with the user's own notes. THIS is
// the unit the user creates - not the page. Several Findings on one page are
// normal and expected (two baptisms in the same register opening), which is
// why saving can never mean "one entry per page".
//
// Name is the find's identity WITHIN its page: changing it means "probably a
// different person", so it prompts; changing the comment does not. Everything
// about the page itself lives in StoredPage, referenced by PageId.
record Finding(Guid Id, Guid PageId, DateTime SavedAt, string Name, string Comment);
