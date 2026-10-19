#!/usr/bin/env python3
"""Regenerate the sample report published at site/sample/orchard/.

    python3 sample.py repos/orchard

Runs the assessment exactly as the README says, keeps the Markdown it prints
as it is (site/sample/orchard/assessment.md), and renders the same text to
HTML with the page's stylesheet. The renderer covers the subset of Markdown
that ReportWriter emits: headings, paragraphs, lists, tables, fenced code,
emphasis, inline code and links. Nothing in the report is edited.
"""

import html
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
OUT = ROOT / "site" / "sample" / "orchard"


def inline(text: str) -> str:
    parts = re.split(r"(`[^`]+`)", text)
    out = []
    for part in parts:
        if part.startswith("`") and part.endswith("`") and len(part) > 1:
            out.append("<code>" + html.escape(part[1:-1]) + "</code>")
            continue
        s = html.escape(part, quote=False)
        s = re.sub(r"\[([^\]]+)\]\((https?://[^)\s]+)\)", r'<a href="\2">\1</a>', s)
        s = re.sub(r"\*\*([^*]+)\*\*", r"<strong>\1</strong>", s)
        s = re.sub(r"(?<![*\w])\*([^*\s][^*]*?)\*(?![*\w])", r"<em>\1</em>", s)
        out.append(s)
    return "".join(out)


def cells(row: str) -> list[str]:
    return [c.strip() for c in row.strip().strip("|").split("|")]


def render(md: str) -> str:
    lines = md.splitlines()
    out: list[str] = []
    i = 0
    while i < len(lines):
        line = lines[i]
        if not line.strip():
            i += 1
            continue
        if line.startswith("```"):
            lang = line[3:].strip()
            i += 1
            body = []
            while i < len(lines) and not lines[i].startswith("```"):
                body.append(lines[i])
                i += 1
            i += 1
            label = ' data-lang="%s"' % html.escape(lang) if lang else ""
            out.append("<pre%s><code>%s</code></pre>" % (label, html.escape("\n".join(body))))
            if lang == "mermaid":
                out.append('<p class="small">Mermaid source, as the report carries it. '
                           "GitHub and most Markdown viewers draw it as a diagram.</p>")
            continue
        m = re.match(r"^(#{1,6}) (.*)$", line)
        if m:
            level = len(m.group(1))
            out.append("<h%d>%s</h%d>" % (level, inline(m.group(2)), level))
            i += 1
            continue
        if line.strip() == "---":
            out.append("<hr>")
            i += 1
            continue
        if line.startswith("|"):
            rows = []
            while i < len(lines) and lines[i].startswith("|"):
                rows.append(lines[i])
                i += 1
            head, rule, body = cells(rows[0]), cells(rows[1]), [cells(r) for r in rows[2:]]
            right = [c.endswith(":") for c in rule]
            def cell(tag, text, k):
                cls = ' class="r"' if k < len(right) and right[k] else ""
                return "<%s%s>%s</%s>" % (tag, cls, inline(text), tag)
            t = ['<div class="table"><table><thead><tr>']
            t += [cell("th", c, k) for k, c in enumerate(head)]
            t.append("</tr></thead><tbody>")
            for r in body:
                t.append("<tr>" + "".join(cell("td", c, k) for k, c in enumerate(r)) + "</tr>")
            t.append("</tbody></table></div>")
            out.append("".join(t))
            continue
        if re.match(r"^\s*([-*]|\d+\.) ", line):
            ordered = bool(re.match(r"^\s*\d+\. ", line))
            items = []
            while i < len(lines) and lines[i].strip():
                if re.match(r"^\s*([-*]|\d+\.) ", lines[i]):
                    items.append(re.sub(r"^\s*([-*]|\d+\.) ", "", lines[i]))
                else:
                    items[-1] += " " + lines[i].strip()
                i += 1
            tag = "ol" if ordered else "ul"
            out.append("<%s>%s</%s>" % (tag, "".join("<li>%s</li>" % inline(x) for x in items), tag))
            continue
        para = []
        while i < len(lines) and lines[i].strip() and not re.match(r"^(#|```|\||---$)", lines[i]):
            para.append(lines[i].strip())
            i += 1
        out.append("<p>%s</p>" % inline(" ".join(para)))
    return "\n".join(out)


PAGE = """<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Sample report: Orchard CMS, assessed by Legacy Lens</title>
<meta name="description" content="The structural assessment Legacy Lens produces, run on Orchard CMS (414,611 lines), published unedited.">
<meta name="color-scheme" content="light dark">
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Montserrat:wght@400;600;700;800;900&amp;display=swap">
<link rel="stylesheet" href="../../assets/site.css">
</head>
<body>

<header class="masthead">
  <div class="wrap bar">
    <a class="brand" href="../../">Legacy Lens</a>
    <ul class="lang" aria-label="Language">
      <li><a href="../../" hreflang="en" lang="en">EN</a></li>
      <li><a href="../../fr/" hreflang="fr" lang="fr">FR</a></li>
      <li><a href="../../de/" hreflang="de" lang="de">DE</a></li>
    </ul>
  </div>
</header>

<main class="wrap">
  <div class="frame">
    <p class="kicker">Sample report</p>
    <p>This is the assessment Legacy Lens writes, run on <a href="https://github.com/OrchardCMS/Orchard">Orchard CMS</a> at commit {commit}, published as the tool printed it. Nothing below was edited.</p>
    <p>Command: <code>dotnet run --project src/LegacyLens.Api -- report repos/orchard</code> · <a href="assessment.md">the Markdown as printed</a></p>
  </div>
  <article class="report">
{body}
  </article>
</main>

<footer class="wrap foot">
  <p><a href="../../">Legacy Lens</a> · open source under the MIT licence.</p>
</footer>

</body>
</html>
"""


def main() -> None:
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    repo = Path(sys.argv[1]).resolve()
    md = subprocess.run(
        ["dotnet", "run", "--project", str(ROOT / "src" / "LegacyLens.Api"), "-c", "Release",
         "--", "report", str(repo)],
        check=True, capture_output=True, text=True).stdout
    if not md.startswith("# "):
        sys.exit("the report did not come back as Markdown")
    commit = subprocess.run(["git", "-C", str(repo), "rev-parse", "--short=8", "HEAD"],
                            check=True, capture_output=True, text=True).stdout.strip()
    OUT.mkdir(parents=True, exist_ok=True)
    (OUT / "assessment.md").write_text(md, encoding="utf-8")
    page = PAGE.replace("{commit}", "<code>%s</code>" % commit).replace("{body}", render(md))
    (OUT / "index.html").write_text(page, encoding="utf-8")
    print("wrote", OUT / "index.html")


if __name__ == "__main__":
    main()
