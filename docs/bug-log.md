# Sketch ProMax bug log

One line per bug, newest at the bottom. Status: **Open** → **Fixed in x.y (please check)** → **Confirmed** once the
reporter has checked the fix.

The add-in downloads this file at every update check (Revit start, then hourly) into
Documents\Sketch ProMax\Bugs\Bug log.md and adds a "Not sent yet" list of the bug report zips on that PC that this log
doesn't cover yet: reports from the day of the newest row on, unless a row names the report by the date and time in
its zip name ("Bug report yyyy-MM-dd HHmm.zip"). Never write model, client, project or person names here: this
repository is public.

| # | Date | What happens | Version | Status |
|---|---|---|---|---|
| 1 | 2026-10-16 | After the in-Revit update to 0.29, the Chat button, Chat tab and tab position don't appear until Revit restarts | 0.29.0 | Open |
| 2 | 2026-10-17 | High: arch_to_structure proposes architectural cladding "columns" and finish/roof build-up floors as structural, and ignores the link's real Structural Columns | 0.28.1 | Open |
| 3 | 2026-10-17 | High: create_raft hosts the raft on the GF level with a big negative offset instead of the foundation level, and ignores the project's type naming | 0.28.1 | Open |
| 4 | 2026-10-17 | High: create_sheets / place_views_on_sheet can't find the drawing area of the company title block, so views overlap the title panel | 0.28.1 | Open |
| 5 | 2026-10-17 | create_plan_views (structural) makes views that behave as looking-up ceiling plans; view range edits fail, upstands draw dashed | 0.28.1 | Open |
| 6 | 2026-10-17 | tag_elements_in_view: two tags per column (column below and above), tags inside the column, floor tags stacked on one point | 0.28.1 | Open |
| 7 | 2026-10-17 | copy_from_file (title blocks) doesn't bind the shared sheet parameters the title block labels use, so fields stay blank | 0.28.1 | Open |
| 8 | 2026-10-17 | loading_plan fails on unbounded rooms one by one; legend is one long text note, not a table | 0.28.1 | Open |
| 9 | 2026-10-17 | export_pdf: unhelpful error when the PDF is open/locked; long exports say "call wait_for_job" but that tool isn't in Claude's tool list | 0.28.1 | Open |
| 10 | 2026-10-17 | run_revit_query_code: changes fail with "no open transaction" instead of being made and rolled back as documented | 0.28.1 | Open |
| 11 | 2026-10-17 | "Revit window stopped responding" three times with no cause given (after family editing / tag probes, once on a simple query) | 0.28.1 | Open |
| 12 | 2026-10-17 | edit_family_text succeeds but returns "referenced object is not valid" | 0.28.1 | Open |
| 13 | 2026-10-17 | link_model: linking a newer-version model returns an internal error instead of a version-mismatch message | 0.28.1 | Open |
| 14 | 2026-10-17 | capture_view_image: zoomed captures render annotation displaced; whole-sheet captures too low-res to read the title panel | 0.28.1 | Open |
| 15 | 2026-10-17 | check_model flags every column "off grid" even when small offsets are intentional; needs a tolerance and exceptions list | 0.28.1 | Open |
| 16 | 2026-10-17 | Missing: 2D reinforcement drawing with the company's detail families (rebar tools only make 3D rebar) | 0.28.1 | Open |
