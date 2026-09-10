import io

P = 'src.html'
s = io.open(P, encoding='utf-8').read()
BS = chr(92)

# 1. box-drawing chars live only in CSS/JS comments -> plain ascii
s = s.replace(u'─', '-')

# 2. escape the remaining non-ascii (all of it sits inside JS string literals),
#    so the page renders correctly no matter what charset the host declares
for ch in [u'·', u'–', u'—', u'←', u'→']:
    s = s.replace(ch, BS + 'u%04X' % ord(ch))

# 3. .meter is a <span> child of a div: inline boxes ignore height, so the
#    2px bar was collapsing and its block child blew the card layout apart
s = s.replace('.meter{height:2px;', '.meter{display:block;height:2px;')

# 4. auto-fill was leaving a dead empty track at the right edge
s = s.replace('repeat(auto-fill,minmax(292px,1fr))', 'repeat(auto-fit,minmax(292px,1fr))')

# 5. <button> may not contain <p>/<ul>; it broke intrinsic sizing. Use div+role.
s = s.replace(
    'return `<button class="rec" aria-pressed="${state.sel===p.id}" data-pick="${p.id}">',
    'return `<div class="rec" role="button" tabindex="0" aria-pressed="${state.sel===p.id}" data-pick="${p.id}">')
s = s.replace('      </button>`;', '      </div>`;')
s = s.replace('.rec{background:var(--surface);', '.rec{background:var(--surface);cursor:pointer;')

# 6. those divs need keyboard activation
s = s.replace('document.addEventListener("change", e => {',
              'document.addEventListener("keydown", e => {' + chr(10) +
              '  const rec = e.target.closest && e.target.closest(".rec");' + chr(10) +
              '  if(rec && (e.key === "Enter" || e.key === " ")){ e.preventDefault(); state.sel = rec.dataset.pick; render(); }' + chr(10) +
              '});' + chr(10) + chr(10) +
              'document.addEventListener("change", e => {')

# 7. hero crops a narrow horizontal band; faces sit lower than I guessed
POS = {'50% 22%': '50% 33%', '52% 24%': '52% 32%', '50% 30%': '50% 38%',
       '50% 20%': '50% 28%', '46% 34%': '46% 40%', '50% 24%': '50% 32%',
       '46% 24%': '46% 26%'}
for old, new in POS.items():
    s = s.replace('pos:"%s"' % old, 'pos:"%s"' % new, 1)

io.open(P, 'w', encoding='utf-8', newline='').write(s)

CHECKS = [
    ('meter display:block', '.meter{display:block;height:2px;'),
    ('grid auto-fit',       'auto-fit,minmax(292px,1fr)'),
    ('rec is a div',        '<div class="rec" role="button" tabindex="0"'),
    ('rec div closed',      '      </div>`;'),
    ('no rec button left',  'button class="rec"'),
    ('keydown handler',     'e.key === "Enter"'),
    ('rec cursor',          '.rec{background:var(--surface);cursor:pointer;'),
    ('gwen repositioned',   'pos:"50% 33%"'),
]
for label, needle in CHECKS:
    hit = needle in s
    want = (label != 'no rec button left')
    print(('OK  ' if hit == want else 'MISS'), label)
print('remaining non-ascii:', sorted(set(c for c in s if ord(c) > 127)))
