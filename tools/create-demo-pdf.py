"""Create an original, one-page filming sample. Requires reportlab and pymupdf."""
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tmp" / "pdfdeps"))
from reportlab.pdfgen import canvas
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.lib.colors import HexColor
import pymupdf

output = ROOT / "output" / "pdf" / "redsheet-demo.pdf"
output.parent.mkdir(parents=True, exist_ok=True)
preview = ROOT / "tmp" / "pdfs"
preview.mkdir(parents=True, exist_ok=True)
pdfmetrics.registerFont(TTFont("Japanese", "C:/Windows/Fonts/YuGothR.ttc", subfontIndex=0))
c = canvas.Canvas(str(output), pagesize=(595.28, 841.89), pageCompression=1)
c.setTitle("RedSheet | 撮影用の暗記サンプル")
c.setAuthor("yosi-0730")
black = HexColor("#111111")
red = HexColor("#e61e1e")

def text(x, y, value, size=15, color=black):
    c.setFont("Japanese", size)
    c.setFillColor(color)
    c.drawString(x, y, value)
    return x + pdfmetrics.stringWidth(value, "Japanese", size)

def blank_line(y, before, answer, after="", size=17):
    x = text(48, y, before + "（", size)
    x = text(x, y, answer, size, red)
    text(x, y, "）" + after, size)

text(48, 788, "RedSheet", 27)
text(48, 757, "赤シートで、答えを隠して思い出す。", 16)
c.setStrokeColor(HexColor("#e8e8e8"))
c.line(48, 733, 547, 733)
blank_line(694, "学生が暗記勉強でよく使う", "赤シート", "を、", 16)
text(48, 664, "PC上で使えるソフトです。", 16)
text(48, 610, "暗記チェック", 17)
text(48, 584, "括弧の中を隠して、答えを思い出してみましょう。", 12)
questions = [
    ("01  日本の首都は", "東京", "。"),
    ("02  水の化学式は", "H2O", "。"),
    ("03  光合成で取り込む気体は", "二酸化炭素", "。"),
    ("04  江戸幕府を開いた人物は", "徳川家康", "。"),
    ("05  英語の apple は", "りんご", "。"),
    ("06  直角の大きさは", "90度", "。"),
]
for i, (before, answer, after) in enumerate(questions):
    y = 528 - i * 62
    blank_line(y, before, answer, after)
    c.setStrokeColor(HexColor("#eeeeee"))
    c.line(48, y - 20, 547, y - 20)
text(48, 102, "シートをクリック → 矢印キーで移動 → 答えを確認", 12)
text(48, 72, "紹介映像用サンプル / RedSheet", 10)
c.showPage()
c.save()
document = pymupdf.open(output)
assert len(document) == 1
page = document[0]
spans = [span for block in page.get_text("dict")["blocks"] if "lines" in block for line in block["lines"] for span in line["spans"]]
red_spans = [span for span in spans if span["color"] == 0xE61E1E]
assert len(red_spans) == 7, red_spans
assert all(0 <= s["bbox"][0] < s["bbox"][2] <= 595.28 and 0 <= s["bbox"][1] < s["bbox"][3] <= 841.89 for s in spans)
page.get_pixmap(matrix=pymupdf.Matrix(1.4, 1.4)).save(preview / "redsheet-demo.png")
print(f"Created {output}; 1 page; 7 red answer spans; text inside page bounds.")
