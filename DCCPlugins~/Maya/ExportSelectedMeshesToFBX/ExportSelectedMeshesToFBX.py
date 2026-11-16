# -*- coding: utf-8 -*-
"""
Maya 2020+  Mesh to FBX  ---  One-click install & run.

HOW TO USE:
  1. Copy this file to your scripts folder, OR run it directly from anywhere:
       execfile(r"full/path/to/ExportSelectedMeshesToFBX.py")
  2. The script auto-installs: a "Tools" menu appears with "Export Meshes to FBX..."
  3. From then on: select meshes > Tools > Export Meshes to FBX... > choose folder > done

HOW TO SHARE:
  Give this single .py file to anyone. They run it once. That's it.
"""

import os
import sys
import maya.cmds as cmds
import maya.mel as mel

MENU_NAME = "arx_tools_menu"
MENU_LABEL = "Tools"
ITEM_LABEL = "Export Meshes to FBX..."
MAYA_SCRIPTS_DIR = os.path.join(cmds.internalVar(userAppDir=True), "scripts")


# ---------------------------------------------------------------------------
#  Core: export logic
# ---------------------------------------------------------------------------

def export_selected_meshes_to_fbx(*args):
    selection = cmds.ls(selection=True, transforms=True)
    if not selection:
        cmds.warning("Please select at least one mesh!")
        return

    export_dir = cmds.fileDialog2(
        dialogStyle=2,
        fileMode=3,
        caption="Select FBX Export Folder",
        okCaption="Export Here"
    )
    if not export_dir:
        cmds.warning("Export cancelled.")
        return
    export_dir = export_dir[0]

    all_meshes = []
    for obj in selection:
        shapes = cmds.listRelatives(obj, shapes=True, fullPath=False, type="mesh")
        if shapes:
            all_meshes.append(obj)
        children = cmds.listRelatives(obj, allDescendents=True, type="transform", fullPath=False)
        if children:
            for child in children:
                child_shapes = cmds.listRelatives(child, shapes=True, fullPath=False, type="mesh")
                if child_shapes and child not in all_meshes:
                    all_meshes.append(child)

    if not all_meshes:
        cmds.warning("No mesh found in selection.")
        return

    original_selection = cmds.ls(selection=True)

    mel.eval('FBXExportSmoothingGroups -v true')
    mel.eval('FBXExportHardEdges -v false')
    mel.eval('FBXExportTangents -v false')
    mel.eval('FBXExportSmoothMesh -v true')
    mel.eval('FBXExportInstances -v false')
    mel.eval('FBXExportReferencedAssetsContent -v false')
    mel.eval('FBXExportInputConnections -v true')
    mel.eval('FBXExportGenerateLog -v false')

    exported = []
    failed = []

    for mesh_name in all_meshes:
        fbx_path = os.path.join(export_dir, mesh_name + ".fbx")
        try:
            cmds.select(mesh_name, replace=True)
            cmds.file(
                fbx_path.replace("\\", "/"),
                force=True,
                options="",
                type="FBX export",
                preserveReferences=True,
                exportSelected=True
            )
            exported.append(mesh_name)
            print("OK  {}  ->  {}".format(mesh_name, fbx_path))
        except Exception as e:
            failed.append(mesh_name)
            cmds.warning("FAILED [{}]: {}".format(mesh_name, str(e)))

    if original_selection:
        cmds.select(original_selection, replace=True)
    else:
        cmds.select(clear=True)

    print("\n" + "=" * 60)
    print("EXPORT FINISHED")
    print("  Success: {}".format(len(exported)))
    print("  Failed:  {}".format(len(failed)))
    print("  Folder:  {}".format(export_dir))
    if failed:
        print("  Failed list: {}".format(", ".join(failed)))
    print("=" * 60)

    cmds.confirmDialog(
        title="Export Complete",
        message="Success: {} files\nFailed:  {} files\n\nFolder: {}".format(
            len(exported), len(failed), export_dir
        ),
        button=["OK"]
    )


# ---------------------------------------------------------------------------
#  Menu registration
# ---------------------------------------------------------------------------

def register_menu():
    unregister_menu()
    main_window = mel.eval('$tmpVar = $gMainWindow')
    if not cmds.menu(MENU_NAME, exists=True):
        cmds.menu(MENU_NAME, label=MENU_LABEL, parent=main_window, tearOff=True)
    cmds.menuItem(
        label=ITEM_LABEL,
        parent=MENU_NAME,
        command=export_selected_meshes_to_fbx,
        annotation="Export each selected mesh to a separate FBX file"
    )
    print("Menu:  {} > {}".format(MENU_LABEL, ITEM_LABEL))


def unregister_menu():
    if cmds.menu(MENU_NAME, exists=True):
        cmds.deleteUI(MENU_NAME, menu=True)


# ---------------------------------------------------------------------------
#  Install: copy self to Maya scripts dir + write userSetup.py
# ---------------------------------------------------------------------------

def install():
    src_path = os.path.abspath(_get_script_path())
    dst_path = os.path.join(MAYA_SCRIPTS_DIR, os.path.basename(src_path))
    setup_path = os.path.join(MAYA_SCRIPTS_DIR, "userSetup.py")

    # Copy script
    if os.path.normcase(os.path.abspath(src_path)) != os.path.normcase(os.path.abspath(dst_path)):
        if not os.path.isdir(MAYA_SCRIPTS_DIR):
            os.makedirs(MAYA_SCRIPTS_DIR)
        with open(src_path, "rb") as f:
            data = f.read()
        with open(dst_path, "wb") as f:
            f.write(data)
        print("Copied:  {}  -->  {}".format(os.path.basename(src_path), MAYA_SCRIPTS_DIR))

    # Write / append userSetup.py
    entry = (
        "import ExportSelectedMeshesToFBX; "
        "maya.utils.executeDeferred(ExportSelectedMeshesToFBX.register_menu)\n"
    )
    marker = "# ExportSelectedMeshesToFBX"

    if os.path.isfile(setup_path):
        with open(setup_path, "r") as f:
            existing = f.read()
        if "ExportSelectedMeshesToFBX" in existing:
            print("userSetup.py already configured. Skipped.")
        else:
            with open(setup_path, "a") as f:
                f.write("\n")
                f.write(marker + "\n")
                f.write(entry)
            print("userSetup.py updated.")
    else:
        with open(setup_path, "w") as f:
            f.write("# -*- coding: utf-8 -*-\n")
            f.write("import maya.utils\n")
            f.write(marker + "\n")
            f.write(entry)
        print("userSetup.py created.")

    print("\nInstall done. Restart Maya for the menu to appear.\n")


# ---------------------------------------------------------------------------
#  Helpers
# ---------------------------------------------------------------------------

def _get_script_path():
    try:
        return __file__
    except NameError:
        for p in sys.path:
            candidate = os.path.join(p, "ExportSelectedMeshesToFBX.py")
            if os.path.isfile(candidate):
                return candidate
        return os.path.join(MAYA_SCRIPTS_DIR, "ExportSelectedMeshesToFBX.py")


# ---------------------------------------------------------------------------
#  Entry points
# ---------------------------------------------------------------------------

def onMayaDroppedPythonFile(*args):
    install()
    register_menu()


def main():
    install()
    register_menu()


if __name__ == "__main__":
    main()
