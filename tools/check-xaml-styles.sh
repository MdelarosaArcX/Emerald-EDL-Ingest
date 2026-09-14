#!/usr/bin/env bash
# Every Style in App.xaml has a TargetType, and WPF matches it exactly: a ToggleButton style
# on a Button throws at window load, not at build. The XAML compiler will not catch it and a
# green build says nothing about it, so this does.
#
# Run from the repository root:  bash tools/check-xaml-styles.sh
set -u

styles=$(mktemp)
usage=$(mktemp)
trap 'rm -f "$styles" "$usage"' EXIT

awk 'match($0, /Style x:Key="([A-Za-z]+)" TargetType="([A-Za-z]+)"/, m) { print m[1]"="m[2] }' \
    src/Emerald.App/App.xaml > "$styles"

for f in src/*/*.xaml; do
    [ "$f" = "src/Emerald.App/App.xaml" ] && continue

    awk -v F="$f" '
        /<[A-Z][A-Za-z]*/ { if (match($0, /<([A-Z][A-Za-z]*)/, e)) elem = e[1] }
        # A standalone Style attribute only. ItemContainerStyle and ColumnHeaderContainerStyle
        # end in "Style" too, and those correctly target the child type rather than the
        # element carrying them - matching them would report every one as a mismatch.
        match($0, /[ \t]Style="\{StaticResource ([A-Za-z]+)\}"/, s) { print F"|"NR"|"elem"|"s[1] }
    ' "$f"
done > "$usage"

bad=$(awk -F= 'NR==FNR { t[$1]=$2; next }
{
    split($0, p, "|");
    key = p[4]; elem = p[3];
    if (!(key in t)) next;                       # defined in the window itself, not App.xaml
    if (t[key] != elem)
        print "  " p[1] ":" p[2] "  <" elem "> uses " key " (TargetType=" t[key] ")";
}' "$styles" "$usage")

if [ -n "$bad" ]; then
    echo "Style/element type mismatches - these throw at window load:"
    echo "$bad"
    exit 1
fi

echo "XAML styles: every use matches its TargetType."
