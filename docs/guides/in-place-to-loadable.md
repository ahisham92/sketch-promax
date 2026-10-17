# Convert an in-place family to a loadable family

In-place families (made with **Model In-Place**) have no loadable family behind them. Sketch ProMax tools that read
family geometry, swap or match types, copy families between models, measure quantities or carbon, check MEP openings, or
cut with voids skip them. Once an element is a loadable family (`.rfa`), those tools can read and use it.

Revit has no command, and its API has no call, that turns an in-place family into a loadable one. You rebuild it by
copying its geometry into a new family. This guide goes through it one step at a time. Expect 5 to 15 minutes per family.

Ask Sketch ProMax to **list the in-place families** first (`in_place_guide`). It lists them by category and level, and
can select them or show them in their own 3D view.

## Before you start

- Save the model, and in a workshared model synchronize, so you can go back if you need to.
- Decide which ones to convert. Convert elements that repeat, or that the tools need to read. A one-off, site-specific
  shape (a sculpted roof or a landform) is often better remodelled with system families (Floor, Roof or Wall by Face).
- Have the family templates ready: **File > New > Family** opens the template folder.

## Steps

1. **Note what the element carries.** Select it and write down its id, category, level and offset, and its Mark,
   Comments, phase, workset and material. Note one exact point you can snap to later, such as a corner.
2. **Copy its geometry.** With it selected, click **Modify | <category> > Model > Edit In-Place**. Drag a window
   around all of its forms and voids (use **Filter** to keep only forms and voids), press **Ctrl+C**, then click
   **Cancel Model** so nothing in the project changes.
3. **Start a new family of the right category.** Go to **File > New > Family** and pick a template:
   - **Metric Generic Model.rft** for most elements.
   - **Metric Generic Model face based.rft** when the element sits on a face (a wall or a slab edge).
   - **Metric Structural Framing**, **Metric Structural Column** or **Metric Column.rft** for beams and columns.

   Walls, floors, roofs and ceilings can't be loadable families. Use Generic Model for them and set the category in step 5.
4. **Paste the geometry.** In the family editor, click **Modify > Clipboard > Paste > Aligned to Current View** (or
   **Aligned to Same Place** when Revit offers it). Then use **Move** to bring the point you noted in step 1 to the
   crossing of the two centre reference planes, with the base on **Ref. Level**. A form that was sketched on picked project
   lines or levels pastes unconstrained, which is fine.
5. **Set the category and behaviour.** Open **Create > Properties > Family Category and Parameters** and set the
   category. Tick **Cut with Voids When Loaded** if its voids should cut walls or floors in the project. Tick **Shared**
   if it will be nested in other families.
6. **Give it a material parameter.** Select the solids. Next to **Material** in Properties, click the small
   **Associate Family Parameter** button and add a type parameter called *Material*. The material then shows in
   schedules, and quantity and carbon tools can read it.
7. **Add sizes if it will be reused (optional).** Add reference planes, dimensions and type parameters (**Label**) for
   the sizes that change between copies.
8. **Save it as an .rfa.** Use **File > Save As > Family**, in your office library, with a name that says what it is.
9. **Load it and place it.** Click **Create > Family Editor > Load into Project and Close** and choose the model. Place
   it at the same point as the in-place element on the same level, using **Align** or snapping. Check it in 3D, for
   example with Sketch ProMax's in-place 3D view (`in_place_guide`, isolate `new_3d_view`).
10. **Copy what the old element carried.** Set Mark, Comments, phase, workset, level and offset as you noted in step 1.
11. **Redo joins and cuts.** Joins and cuts made with the in-place element are not carried over. Use **Modify > Geometry
    > Join** or **Cut** on the new instance. Sketch ProMax gives these steps with `in_place_guide` action `join` or `cut`.
12. **Delete the in-place element.** Tags on it are deleted with it, so tag the new instance. Check the schedules
    that listed it.
13. **Do the copies.** Each copy of an in-place element is its own in-place family. Convert one, then delete the
    copies and place the new family at each spot. Revit's **Copy** or **Array** is quickest when they are evenly spaced.
14. **Check.** Ask Sketch ProMax to list in-place families again. The count should drop, and the tools now read the new
    family.

## If something goes wrong

- **Paste is greyed out:** the clipboard was cleared. Go back to step 2 and copy again from Edit In-Place.
- **Geometry lands far from the reference planes:** this is normal for Aligned to Current View. Move it as in step 4.
  A family whose geometry is kilometres from its origin is slow and hard to place.
- **The void does not cut once loaded:** turn on **Cut with Voids When Loaded** (step 5) and reload. Then use
  **Modify > Cut** on the host.
- **The category you need is missing:** walls, floors, roofs, ceilings, stairs and railings can't be loadable families.
  Use Generic Model, or remodel the element with the matching system family.
