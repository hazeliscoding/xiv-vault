// Draws docs/brand/lockup.svg and lockup-dark.svg: the diamond mark beside the "XIV Vault"
// wordmark, set in IBM Plex Sans SemiBold and converted to paths so no font is needed to view it.
//
//   npm i --prefix .brand opentype.js@1
//   node scripts/make-lockup.mjs .brand/node_modules/opentype.js
import { createRequire } from "node:module";
import { writeFileSync } from "node:fs";
import { join, resolve } from "node:path";

const opentypePath = process.argv[2];
if (!opentypePath) {
  console.error("usage: node scripts/make-lockup.mjs <path to the opentype.js package>");
  process.exit(1);
}

const opentype = createRequire(import.meta.url)(resolve(opentypePath));
const font = opentype.loadSync("src/XivVault.Desktop/Assets/Fonts/IBMPlexSans-SemiBold.ttf");
const size = 40;
const text = "XIV Vault";

// Page-title tracking from the mockup: -0.02em.
const path = new opentype.Path();
const glyphs = font.stringToGlyphs(text);
let x = 0;
for (let i = 0; i < glyphs.length; i++) {
  path.extend(glyphs[i].getPath(x, 0, size));
  let advance = (glyphs[i].advanceWidth * size) / font.unitsPerEm;
  if (i < glyphs.length - 1) advance += (font.getKerningValue(glyphs[i], glyphs[i + 1]) * size) / font.unitsPerEm;
  x += advance - 0.02 * size;
}

const box = path.getBoundingBox();
const width = Math.ceil(42 + box.x2 + 4);
const data = path.toPathData(2);
const diamond = (stroke) => `<rect x="9.4" y="9.9" width="21.2" height="21.2" rx="1.5" transform="rotate(45 20 20.5)" fill="none" stroke="${stroke}" stroke-width="3"`;

const svg = (stroke, fill, glow) => `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${width} 41" width="${width}" height="41">
  <defs>
    <filter id="glow" x="-50%" y="-50%" width="200%" height="200%">
      <feGaussianBlur stdDeviation="2"/>
    </filter>
  </defs>
${glow ? `  ${diamond(stroke)} opacity="0.45" filter="url(#glow)"/>\n` : ""}  ${diamond(stroke)}/>
  <path transform="translate(42 35.5)" fill="${fill}" d="${data}"/>
</svg>
`;

writeFileSync(join("docs", "brand", "lockup.svg"), svg("#2F63D0", "#0A0C12", false));
writeFileSync(join("docs", "brand", "lockup-dark.svg"), svg("#6C9EFF", "#E8ECF1", true));
console.log(`Wrote lockup.svg and lockup-dark.svg (${width} x 41)`);
