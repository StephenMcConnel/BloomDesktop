// This file exposes some utility functions that are needed in both iframes. The idea is
// to make them available to import with a minimum of dependencies.

import { getEditablePageBundleExports } from "../../editViewFrame";
import { CanvasElementManager } from "../../js/CanvasElementManager";

export const kCanvasElementClass = "bloom-canvas-element";
export const kCanvasElementSelector = `.${kCanvasElementClass}`;
export const kHasCanvasElementClass = "bloom-has-canvas-element";

// Enhance: we could reduce cross-bundle dependencies by separately defining the CanvasElementManager interface
// and just importing that here.
export function getCanvasElementManager(): CanvasElementManager | undefined {
    const editablePageBundleExports = getEditablePageBundleExports();
    return editablePageBundleExports
        ? editablePageBundleExports.getTheOneCanvasElementManager()
        : undefined;
}
