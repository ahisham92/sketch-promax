# Sketch ProMax bug log

One line per bug, newest at the bottom. Status: **Open** → **Fixed in x.y (please check)** → **Confirmed** once the
reporter has checked the fix.

The add-in downloads this file at every update check (Revit start, then hourly) into
Documents\Sketch ProMax\Bugs\Bug log.md and adds a "Not sent yet" list of the bug report zips on that PC that this log
doesn't cover yet: a report counts as sent once a row below names it by the date and time in its zip name
("Bug report yyyy-MM-dd HHmm.zip"). Never write model, client, project or person names here: this repository is public.

| # | Date | What happens | Version | Status |
|---|---|---|---|---|
| 1 | 2026-10-16 | After the in-Revit update to 0.29, the Chat button, Chat tab and tab position don't appear until Revit restarts | 0.29.0 | Fixed in 0.30 (please check) |
| 2 | 2026-10-17 | High: arch_to_structure proposes architectural cladding "columns" and finish/roof build-up floors as structural, and ignores the link's real Structural Columns | 0.28.1 | Fixed in 0.30 (please check) |
| 3 | 2026-10-17 | High: create_raft hosts the raft on the GF level with a big negative offset instead of the foundation level, and ignores the project's type naming | 0.28.1 | Fixed in 0.30 (please check) |
| 4 | 2026-10-17 | High: create_sheets / place_views_on_sheet can't find the drawing area of the company title block, so views overlap the title panel | 0.28.1 | Fixed in 0.30 (please check) |
| 5 | 2026-10-17 | create_plan_views (structural) makes views that behave as looking-up ceiling plans; view range edits fail, upstands draw dashed | 0.28.1 | Fixed in 0.30 (please check) |
| 6 | 2026-10-17 | tag_elements_in_view: two tags per column (column below and above), tags inside the column, floor tags stacked on one point | 0.28.1 | Fixed in 0.30 (please check) |
| 7 | 2026-10-17 | copy_from_file (title blocks) doesn't bind the shared sheet parameters the title block labels use, so fields stay blank | 0.28.1 | Fixed in 0.30 (please check) |
| 8 | 2026-10-17 | loading_plan fails on unbounded rooms one by one; legend is one long text note, not a table | 0.28.1 | Fixed in 0.30 (please check) |
| 9 | 2026-10-17 | export_pdf: unhelpful error when the PDF is open/locked; long exports say "call wait_for_job" but that tool isn't in Claude's tool list | 0.28.1 | Fixed in 0.30 (please check) |
| 10 | 2026-10-17 | run_revit_query_code: changes fail with "no open transaction" instead of being made and rolled back as documented | 0.28.1 | Fixed in 0.30 (please check) |
| 11 | 2026-10-17 | "Revit window stopped responding" three times with no cause given (after family editing / tag probes, once on a simple query) | 0.28.1 | Fixed in 0.30 (please check) |
| 12 | 2026-10-17 | edit_family_text succeeds but returns "referenced object is not valid" | 0.28.1 | Fixed in 0.30 (please check) |
| 13 | 2026-10-17 | link_model: linking a newer-version model returns an internal error instead of a version-mismatch message | 0.28.1 | Fixed in 0.30 (please check) |
| 14 | 2026-10-17 | capture_view_image: zoomed captures render annotation displaced; whole-sheet captures too low-res to read the title panel | 0.28.1 | Fixed in 0.30 (please check) |
| 15 | 2026-10-17 | check_model flags every column "off grid" even when small offsets are intentional; needs a tolerance and exceptions list | 0.28.1 | Fixed in 0.30 (please check) |
| 16 | 2026-10-17 | Missing: 2D reinforcement drawing with the company's detail families (rebar tools only make 3D rebar) | 0.28.1 | Fixed in 0.30 (please check) |
| 17 | 2026-10-17 | Revit asks about the unsigned add-in on every start even after "Always Load" (the loader's location and bytes changed with each update) | 0.29.0 | Fixed in 0.30 (please check) |
| 18 | 2026-10-17 | Opening a model feels slow while Sketch ProMax loads; startup work should not delay model opening | 0.30.0 | Fixed in 0.31 (please check) |
| 19 | 2026-10-17 | Chat panel looks basic; needs a more polished, smoother chat (rendering, typing, scrolling) | 0.30.0 | Fixed in 0.31 (please check) |
| 20 | 2026-10-17 | arch_to_structure returns "Nothing was made." with no reason; likely the new cladding filter skips everything in the test model | 0.30.0 | Fixed in 0.31 (please check) |
| 21 | 2026-10-17 | Chat panel needs: chat history sidebar with search, model picker (Opus/Sonnet/Haiku/Fable), home screen with feature library, saved prompts and tailored prompts | 0.30.0 | Fixed in 0.31 (please check) |
| 22 | 2026-10-17 | New: value engineering tools (find savings, compare alternatives, apply an option, VE register) | 0.30.0 | Fixed in 0.31 (please check) |
| 23 | 2026-10-17 | The update check fails when GitHub's API times out or refuses, instead of falling back to the github.com releases page | 0.33.0 | Fixed in 0.33.3 (please check) |
| 24 | 2026-10-17 | Loading a family with a bad type catalog stops Revit on a "Could not parse column header" dialog | 0.33.0 | Fixed in 0.33.3 (please check) |
| 25 | 2026-10-17 | Chat crashes drawing the icon on every reply header (frozen geometry transformed in place) | 0.33.0 | Fixed in 0.33.3 (please check) |
| 26 | 2026-10-17 | title_block_from_cad fails trying to delete template lines Revit doesn't allow to be deleted | 0.33.0 | Fixed in 0.33.3 (please check) |
| 27 | 2026-10-17 | find_clashes ignores void-only cutting families (they have no bounding box of their own) | 0.33.0 | Confirmed (the 0.34.45 field test rebuilt the void and found the clash) |
| 28 | 2026-10-17 | The issue check stops with an error when one of its checks crashes, instead of naming the step and carrying on | 0.33.0 | Fixed again in next release (the 0.34.45 field test's issue check no longer crashed, but its probe view wasn't placed on the sheet: a view whose only note was just made counts as empty until Revit regenerates) |
| 29 | 2026-10-17 | Redlines are matched to the wrong place on the sheet (markups worked out before the sheet was drawn, viewport mapping off) | 0.33.0 | Fixed in next release, please check (likely cause: a sheet with no title block has an outline that doesn't hold its viewports, so markups came out past the page; the page is now the viewports' extent there, and the report prints the frame if it still fails) |
| 30 | 2026-10-17 | The chat message box looks like two boxes and typed text starts at the bottom, not where the hint was | 0.33.3 | Fixed in 0.33.4 (please check) |
| 31 | 2026-10-17 | The chat shows a "finish your edit mode" card when Revit is only busy, not in an edit mode | 0.33.3 | Fixed in 0.33.4 (please check) |
| 32 | 2026-10-17 | Too many approval dialogs: Revit asks before every small change | 0.33.4 | Fixed in 0.33.5 (please check) |
| 33 | 2026-10-17 | After an in-Revit update the chat opens empty instead of the chat that was open | 0.33.5 | Fixed in 0.33.6 (please check) |
| 34 | 2026-10-18 | Shared models: Revit shows "Unable to Access the Model … is not currently available" again and again while Sketch ProMax works | 0.34.1 | Fixed in 0.34.2 (please check) |
| 35 | 2026-10-18 | After Shrink chat, the chat rereads the old conversation file (steps named like a long code ".jsonl") and loses the saving | 0.34.0 | Fixed in 0.34.2 (please check) |
| 36 | 2026-10-22 | Revit closes by itself during the field test, at the purge_unused preview (twice, Revit 2024) | 0.34.38 | Fixed in 0.34.40, please check (the preview deletes nothing; families are purged one at a time; a run Revit closes during is reported and can be continued) |
| 37 | 2026-10-22 | Revit closed by itself while the chat ran a code query, and nothing recorded which call it was | 0.34.38 | Fixed in 0.34.40, please check (the next start logs which call was running) |
| 38 | 2026-10-22 | export_pdf: an internal check print was refused because of thousands of model-wide issue-check items; the chat had to write its own export code | 0.34.38 | Fixed in 0.34.40, please check (check_print) |
| 39 | 2026-10-22 | export_pdf can't use a saved View/Sheet Set or a saved PDF export setup (A0, colour, 100%), so the chat wrote its own export code | 0.34.38 | Fixed in 0.34.40, please check (sheet_set, pdf_setup) |
| 40 | 2026-10-22 | The chat sometimes calls a Revit tool before it is loaded ("No such tool available") and has to try again (twice in one day) | 0.34.38 | Confirmed (the 0.34.45 field test's new chat had the Revit tools before its first message) |
| 41 | 2026-10-23 | query_elements: a family asked in the wrong category (sheet piles and casings as Structural Columns, manholes as Generic Models) only says "no type matches", four times in the chats | 0.34.45 | Fixed in next release, please check (it names the categories where types of that name are) |
| 42 | 2026-10-23 | list_types and other tools refuse "Detail Groups" and "Slab Edges" as unknown categories | 0.34.45 | Fixed in next release, please check (accepted; an unknown name lists the close ones) |
| 43 | 2026-10-23 | capture_view_image can zoom to elements only in plans; in sections and on detail items it says they have no location (three times) | 0.34.45 | Fixed in next release, please check (sections, elevations and detail views too, and elements seen only in the view) |
| 44 | 2026-10-23 | duplicate_view_to_location: Revit refuses the whole copy when a copied dimension loses its references in the new place (five sections in one go) | 0.34.45 | Fixed in next release, please check (those dimensions are left out and listed) |
| 45 | 2026-10-23 | arrange_tags says "No tags found" in a view whose pile levels are spot elevations, with no hint why | 0.34.45 | Fixed in next release, please check (it says what the view has instead) |
