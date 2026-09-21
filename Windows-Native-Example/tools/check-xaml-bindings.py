#!/usr/bin/env python3
"""Fails when a XAML binding would throw at runtime on the first window that shows it.

WPF cannot run on macOS/Linux, so this catches the one mistake `dotnet build` cannot: a
control property that binds TWO-WAY BY DEFAULT (TextBox.Text, DatePicker.SelectedDate /
DisplayDateEnd, CheckBox.IsChecked, ComboBox.SelectedItem, ...) bound to a read-only
view-model property without `Mode=OneWay`. WPF throws InvalidOperationException
("A TwoWay or OneWayToSource binding cannot work on the read-only property ...") when the
view loads.

    python3 tools/check-xaml-bindings.py
"""
import glob
import os
import re
import sys
import xml.etree.ElementTree as ET

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src")
APP = os.path.join(ROOT, "AmwalECR.Example")
CORE = os.path.join(ROOT, "AmwalECR.Example.Core")

TWO_WAY = {
    "TextBox": {"Text"},
    "ComboBox": {"Text", "SelectedItem", "SelectedValue", "SelectedIndex", "IsDropDownOpen"},
    "ListBox": {"SelectedItem", "SelectedValue", "SelectedIndex"},
    "ListView": {"SelectedItem", "SelectedValue", "SelectedIndex"},
    "DataGrid": {"SelectedItem", "SelectedValue", "SelectedIndex"},
    "TabControl": {"SelectedItem", "SelectedIndex", "SelectedValue"},
    "CheckBox": {"IsChecked"},
    "RadioButton": {"IsChecked"},
    "ToggleButton": {"IsChecked"},
    "Expander": {"IsExpanded"},
    "DatePicker": {"SelectedDate", "DisplayDate", "DisplayDateStart", "DisplayDateEnd", "IsDropDownOpen", "Text"},
    "Calendar": {"SelectedDate", "DisplayDate", "DisplayDateStart", "DisplayDateEnd"},
    "Slider": {"Value"},
    "ScrollBar": {"Value"},
}


def view_model_properties():
    settable, readonly = set(), set()
    files = glob.glob(os.path.join(APP, "ViewModels", "*.cs")) + glob.glob(
        os.path.join(CORE, "**", "*.cs"), recursive=True
    )
    for path in files:
        if os.sep + "obj" + os.sep in path:
            continue
        text = open(path, encoding="utf-8").read()
        # CommunityToolkit [ObservableProperty] private T _name;  ->  Name (settable)
        for m in re.finditer(r"\[ObservableProperty\][^;]*?\s_?(\w+)\s*(?:=[^;]*)?;", text):
            name = m.group(1).lstrip("_")
            settable.add(name[0].upper() + name[1:])
        for m in re.finditer(r"public\s+[\w<>\?\[\],\.]+\s+(\w+)\s*\{([^}]*)\}", text):
            (settable if re.search(r"\bset\b|\binit\b", m.group(2)) else readonly).add(m.group(1))
        for m in re.finditer(r"public\s+[\w<>\?\[\],\.]+\s+(\w+)\s*=>", text):
            readonly.add(m.group(1))
    return settable, readonly


def main():
    settable, readonly = view_model_properties()
    bad, inspected = [], 0
    for path in glob.glob(os.path.join(APP, "**", "*.xaml"), recursive=True):
        if os.sep + "obj" + os.sep in path:
            continue
        for el in ET.parse(path).getroot().iter():
            tag = el.tag.split("}")[-1]
            for attr, value in el.attrib.items():
                prop = attr.split("}")[-1].split(".")[-1]
                if not value.startswith("{Binding") or prop not in TWO_WAY.get(tag, ()):
                    continue
                inspected += 1
                if re.search(r"Mode\s*=\s*(OneWay|OneTime|OneWayToSource)", value):
                    continue
                m = re.match(r"\{Binding\s+(?:Path\s*=\s*)?([\w\.\[\]]+)", value)
                if m and m.group(1).split(".")[-1] in readonly - settable:
                    bad.append(f"{os.path.relpath(path, ROOT)}: <{tag} {prop}> binds read-only '{m.group(1)}' two-way; add Mode=OneWay")
    print(f"{inspected} two-way-by-default bindings inspected")
    for line in bad:
        print("ERROR", line)
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
