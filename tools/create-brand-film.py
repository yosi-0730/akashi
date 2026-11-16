"""Render editable brand film artboards from brand-film.json (PDF + review PNGs)."""
from pathlib import Path
import json
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tmp" / "pdfdeps"))
from reportlab.pdfgen import canvas
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.lib.colors import HexColor
from reportlab.graphics.barcode.qr import QrCodeWidget
from reportlab.graphics.shapes import Drawing
from reportlab.graphics import renderPDF
import pymupdf
from PIL import Image, ImageOps, ImageDraw

for name, path in {
    "JP": "YuGothR.ttc", "JPBold": "YuGothB.ttc",
    "Latin": "segoeuil.ttf", "LatinBold": "segoeuib.ttf",
}.items():
    pdfmetrics.registerFont(TTFont(name, f"C:/Windows/Fonts/{path}", subfontIndex=0))

spec = json.loads((ROOT / "tools" / "brand-film.json").read_text(encoding="utf-8-sig"))
width, height = spec.get("size", [1280, 720])
output = ROOT / "output" / "pdf" / "redsheet-brand-film.pdf"
output.parent.mkdir(parents=True, exist_ok=True)
preview = ROOT / "tmp" / "pdfs" / "brand-film"
preview.mkdir(parents=True, exist_ok=True)
pdf = canvas.Canvas(str(output), pagesize=(width, height), pageCompression=1)
pdf.setTitle(spec["title"])
pdf.setAuthor("yosi-0730")
for page in spec["pages"]:
    pdf.setFillColor(HexColor(page.get("background", "#ffffff")))
    pdf.rect(0, 0, width, height, fill=1, stroke=0)
    for element in page["elements"]:
        kind = element["kind"]
        color = HexColor(element.get("color", "#151515"))
        x, y = element.get("x", 0), element.get("y", 0)
        if kind == "text":
            font, size, value = element.get("font", "JP"), element["size"], element["text"]
            measured = pdfmetrics.stringWidth(value, font, size)
            if element.get("align") == "center": x -= measured / 2
            if element.get("align") == "right": x -= measured
            assert 0 <= x and x + measured <= width + 1, (value, x, measured)
            pdf.setFont(font, size)
            pdf.setFillColor(color)
            pdf.drawString(x, height - y, value)
        elif kind == "rule":
            pdf.setStrokeColor(color)
            pdf.setLineWidth(element.get("weight", 1))
            pdf.line(x, height-y, element["x2"], height-element.get("y2", y))
        elif kind == "rect":
            pdf.setFillColor(color)
            pdf.rect(x, height-y-element["height"], element["width"], element["height"], fill=1, stroke=0)
        elif kind == "qr":
            qr = QrCodeWidget(element["url"], barFillColor=color, barStrokeColor=color, barBorder=4)
            bounds = qr.getBounds()
            side = element["size"]
            drawing = Drawing(side, side, transform=[side/(bounds[2]-bounds[0]), 0, 0, side/(bounds[3]-bounds[1]), 0, 0])
            drawing.add(qr)
            renderPDF.draw(drawing, pdf, x, height-y-side)
        else:
            raise ValueError(kind)
    pdf.showPage()
pdf.save()

doc = pymupdf.open(output)
assert len(doc) == len(spec["pages"])
images = []
copy = []
for index, page in enumerate(doc):
    for block in page.get_text("dict")["blocks"]:
        for line in block.get("lines", []):
            for span in line["spans"]:
                assert 0 <= span["bbox"][0] < span["bbox"][2] <= width+1 and 0 <= span["bbox"][1] < span["bbox"][3] <= height+1, span
                copy.append(span["text"])
    path = preview / f"page-{index+1:02}.png"
    page.get_pixmap(matrix=pymupdf.Matrix(1.25, 1.25)).save(path)
    image = Image.open(path).convert("RGB")
    image.thumbnail((768, 432))
    images.append(image)
sheet = Image.new("RGB", (792, len(images)*456+24), "#d9d9d9")
for index, image in enumerate(images): sheet.paste(image, (12, 12+index*456))
sheet.save(preview / "contact-sheet.png")
(preview / "copy.txt").write_text("\n".join(copy), encoding="utf-8")
print(f"Created {output}; {len(doc)} pages; all text within artboards.")
