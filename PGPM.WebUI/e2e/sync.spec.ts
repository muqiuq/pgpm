import { test } from '@playwright/test'

/**
 * The sync core in two browsers (docs/05 §3 phase 2 "done when", docs/03 §11.13): two contexts = two users (or two
 * devices of one user) against the e2e stack. Written out at the integration with the server; until then each scenario
 * is a fixme with the steps it will take. Shared setup: the admin creates the users Ana and Ben, both members of the
 * group "Innovation team" with Notes: Create; each context signs in as one of them (signIn from ./helpers).
 */

test.describe('sync between two browsers', () => {
  test.fixme('a note created by Ana appears at Ben in under a second, and Ben’s edit at Ana', async () => {
    // Ana: Notes → New note "Live note", Share with Innovation team (edit, preselected) → Create.
    // Ben: the Notes list shows "Live note" within 1 s (no reload); open it, edit the title inline.
    // Ana: the page shows Ben's title within 1 s; the sync indicators of both say "Live", no pending count.
  })

  test.fixme('offline edits sync after reconnecting; the same field becomes a conflict resolved in the dialog', async () => {
    // Both open the shared note. Ben: context.setOffline(true) → indicator "Offline".
    // Ana changes the title to "A"; Ben (offline) changes the title to "B" and pins the note → indicator shows 1 pending.
    // Ben: setOffline(false) → pin applied for both; indicator turns orange "1 conflict", a toast says so once.
    // Ben: click the indicator → Conflicts tab lists the note: Yours "B", Theirs "A" by Ana → Keep mine.
    // Ana: the title becomes "B" live; Ben's indicator has no conflict; History (online) shows the resolution.
  })

  test.fixme('Keep theirs for all, and Edit of the content opens the Markdown editor in its conflict view', async () => {
    // Two notes changed on both sides while Ben is offline (title of one, content of the other).
    // Ben: Conflicts → Edit on the content → /notes/:id/edit?conflict=… shows Their changes / My changes;
    // Save mine anyway → Ana sees Ben's content. Then Keep theirs for all → no conflicts left.
  })

  test.fixme('deleted by Ana while Ben edits offline: Restore with my changes brings it back everywhere', async () => {
    // Ben offline edits the note; Ana deletes it (⋯ → Delete note…) → it is in Ana's Trash.
    // Ben online → conflict "Deleted": Restore with my changes → the note is back for both with Ben's change,
    // gone from the Trash.
  })

  test.fixme('Ben deletes what Ana changed meanwhile: Delete anyway, or Keep it', async () => {
    // Ana gives Ben manage (Share dialog); Ben offline deletes; Ana edits; Ben online → "Changed" conflict:
    // Keep it → the note stays with Ana's change; repeat and Delete anyway → it goes for both.
  })

  test.fixme('unsharing removes the note from Ben’s device live; restoring sharing brings it back', async () => {
    // Ana: Share dialog → Innovation team: No access → Save (online only; offline the dialog says so).
    // Ben: the note disappears from the list and its open page says it is not on this device; a toast says why;
    // IndexedDB (page.evaluate) has no row, no shares, no draft of it.
    // Ana shares again with read → Ben sees it read-only (no Edit, inline fields read-only).
  })

  test.fixme('a reset (group membership changed by the admin) re-pulls without losing pending changes', async () => {
    // Ben offline creates a note; the admin removes Ben from a group and adds him back (access epoch +1).
    // Ben online → the pending note is pushed first, the local data is re-pulled, conflicts stay.
  })

  test.fixme('Trash: restore and purge, online only', async () => {
    // Ana deletes a note → Trash lists it with "Ana" and the time → Restore → back in Notes for Ana and Ben.
    // The admin purges another entry permanently; offline the Trash says "Available online only".
  })

  test.fixme('sign-out wipes the device; another user signing in never sees the previous user’s data', async () => {
    // Ana with a pending change signs out → the warning names it → Sign out → IndexedDB "pgpm" is gone.
    // Session expired (cookie cleared) → Ben signs in on the same context → Ana's notes are not shown, data wiped.
  })

  test.fixme('two tabs of one user: one runs the sync (Web Lock), the other follows and takes over', async () => {
    // Two pages in one context: only one WebSocket to /api/v1/sync/live; an edit in the follower tab is pushed by the
    // leader within a second; closing the leader tab → the follower opens the live channel.
  })

  test.fixme('the editor shows who else is editing, and a live change of the text before saving', async () => {
    // Ana and Ben open /notes/:id/edit → each sees "… is editing this (since hh:mm)".
    // Ana saves; Ben's editor shows Their changes / My changes at once → Take theirs.
  })
})
