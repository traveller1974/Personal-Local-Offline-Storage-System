"""Run the real source or bundled worker against printable Chinese invoices."""
import argparse
import json
from pathlib import Path
import subprocess
import sys
from PIL import Image, ImageDraw, ImageFont, ImageFilter


def samples(directory):
    directory.mkdir(parents=True, exist_ok=True)
    font_path = Path('C:/Windows/Fonts/msyh.ttc')
    font = ImageFont.truetype(str(font_path), 30)
    header_font = ImageFont.truetype(str(font_path), 38)
    cases = [('打印货单', False, False), ('双数量列', True, False), ('轻微倾斜', False, True)]
    paths = []
    for label, multiple, angled in cases:
        image = Image.new('RGB', (1300, 640), 'white')
        draw = ImageDraw.Draw(image)
        draw.text((420, 35), '厂家送货单', font=header_font, fill='black')
        draw.text((70, 105), '供货方：测试五金厂', font=font, fill='black')
        columns = [70, 430, 640, 850, 1080]
        headers = ['品名', '规格', '数量', '实收数量' if multiple else '单价', '金额']
        rows = [headers, ['螺丝', 'M8', '5', '4' if multiple else '12', '60'],
                ['螺帽', 'M10', '8', '7' if multiple else '20', '160'], ['合计', '', '13', '', '220']]
        for row_index, cells in enumerate(rows):
            y = 190 + row_index * 90
            for x, cell in zip(columns, cells):
                draw.text((x + 8, y + 20), cell, font=font, fill='black')
            draw.line((60, y, 1250, y), fill='#666666', width=2)
        draw.line((60, 550, 1250, 550), fill='#666666', width=2)
        for x in columns + [1250]:
            draw.line((x - 10, 190, x - 10, 550), fill='#666666', width=2)
        if angled:
            image = image.rotate(2, resample=Image.Resampling.BICUBIC, expand=True, fillcolor='white').filter(ImageFilter.GaussianBlur(0.3))
        path = directory / (label + '.png')
        image.save(path)
        paths.append(path)
    return paths


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--worker', type=Path)
    parser.add_argument('--output', type=Path, default=Path(__file__).resolve().parents[1] / 'artifacts/ocr')
    args = parser.parse_args()
    paths = samples(args.output / '中文 含空格样本')
    command = [str(args.worker)] if args.worker else [sys.executable, str(Path(__file__).with_name('worker.py'))]
    requests = [{'protocolVersion': 1, 'requestId': str(i), 'imagePath': str(p.resolve())} for i, p in enumerate(paths)]
    requests += [{'protocolVersion': 1, 'requestId': 'missing', 'imagePath': str(args.output / '不存在.png')},
                 {'protocolVersion': 9, 'requestId': 'bad-protocol', 'imagePath': str(paths[0].resolve())},
                 {'protocolVersion': 1, 'requestId': 'format', 'imagePath': str(Path(__file__).resolve())}]
    result = subprocess.run(command, input='\n'.join(json.dumps(r, ensure_ascii=False) for r in requests) + '\n',
                            text=True, encoding='utf-8', capture_output=True, timeout=150, cwd=str(args.output))
    responses = [json.loads(line) for line in result.stdout.splitlines()]
    (args.output / ('bundled-results.json' if args.worker else 'source-results.json')).write_text(
        json.dumps({'command': command, 'responses': responses, 'stderr': result.stderr}, ensure_ascii=False, indent=2), encoding='utf-8')
    assert result.returncode == 0, result.stderr
    assert len(responses) == len(requests), result.stdout
    for response in responses[:3]:
        assert response['success'], response
        texts = [b['text'] for b in response['blocks']]
        assert any('螺丝' in t for t in texts), texts
        assert any(t.strip() == '5' for t in texts), texts
        assert all(len(b['box']) == 4 and 0 <= b['confidence'] <= 1 for b in response['blocks'])
    assert responses[3]['errorCode'] == 'IMAGE_MISSING'
    assert responses[4]['errorCode'] == 'PROTOCOL_INVALID'
    assert responses[5]['errorCode'] == 'FORMAT_UNSUPPORTED'
    print('PASS: 3 real Chinese invoice OCR cases; UTF-8 paths; missing image, protocol and format errors.')


if __name__ == '__main__':
    main()
