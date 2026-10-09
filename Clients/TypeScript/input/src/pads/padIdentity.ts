/**
 * Controller identification from Gamepad.id. Every browser formats the id differently:
 *   Chrome/Edge: "Wireless Controller (STANDARD GAMEPAD Vendor: 054c Product: 0ce6)",
 *                "Xbox 360 Controller (XInput STANDARD GAMEPAD)" for every XInput pad on Windows
 *   Firefox:     "054c-0ce6-Wireless Controller", "xinput" for XInput pads on Windows
 *   Safari:      "54c-ce6-DualSense Wireless Controller" (no zero padding) or a bare product name
 * so we look for a vendor/product pair first and fall back to name keywords. Pure, unit-tested.
 */

/** Button label style: Xbox (A B X Y), PlayStation (✕ ○ □ △), Nintendo (B A Y X), generic (Xbox style). */
export type PadFamily = 'xbox' | 'playstation' | 'nintendo' | 'generic';

/** Maker shown next to a detected controller (brand mark), or null for unbranded / third-party pads. */
export type PadBrand = 'playstation' | 'xbox' | 'nintendo' | 'steam';

export interface PadIdentity {
  family: PadFamily;
  /** Friendly name for toasts and settings, e.g. "Xbox Controller", "DualSense". */
  name: string;
  brand: PadBrand | null;
  /** Lowercase 4-digit hex USB vendor/product ids when the browser exposes them. */
  vendor: string | null;
  product: string | null;
}

const MICROSOFT = '045e';
const SONY = '054c';
const NINTENDO = '057e';
const VALVE = '28de';
const LOGITECH = '046d';
const EIGHTBITDO = '2dc8';
/** XInput third-party pads: PDP, PowerA, PowerA/BDA, Mad Catz, Hori, Razer, Nacon, Hyperkin, Rock Candy. */
const XINPUT_CLONES = new Set(['0e6f', '20d6', '24c6', '0738', '0f0d', '1532', '146b', '3285', '2e24', '1bad']);

const XBOX_360 = new Set(['028e', '028f', '0291', '02a1', '0719']);
const XBOX_ELITE = new Set(['02e3', '0b00', '0b05', '0b22']);
const XBOX_SERIES = new Set(['0b12', '0b13']);
const DUALSENSE = new Set(['0ce6']);
const DUALSENSE_EDGE = new Set(['0df2']);
const DUALSHOCK4 = new Set(['05c4', '09cc', '0ba0']);
const LOGITECH_NAMES: Record<string, string> = {
  c21d: 'Logitech F310',
  c216: 'Logitech F310',
  c21e: 'Logitech F510',
  c218: 'Logitech F510',
  c21f: 'Logitech F710',
  c219: 'Logitech F710',
};

/** Vendor/product from any of the id formats above, or nulls. */
export function parseVendorProduct(id: string): { vendor: string | null; product: string | null } {
  const chrome = /vendor:\s*([0-9a-f]{1,4})\s+product:\s*([0-9a-f]{1,4})/i.exec(id);
  const dashed = chrome ? null : /^\s*([0-9a-f]{1,4})-([0-9a-f]{1,4})-/i.exec(id);
  const m = chrome ?? dashed;
  if (!m) return { vendor: null, product: null };
  const pad = (s: string) => s.toLowerCase().padStart(4, '0');
  return { vendor: pad(m[1]!), product: pad(m[2]!) };
}

export function identifyPad(id: string): PadIdentity {
  const { vendor, product } = parseVendorProduct(id);
  const s = id.toLowerCase();
  const out = (family: PadFamily, name: string, brand: PadBrand | null = null): PadIdentity => ({ family, name, brand, vendor, product });
  const p = product ?? '';

  // Vendor ids first: they survive renamed / localized product strings.
  if (vendor === SONY || /dualsense|dualshock|playstation|ps[345] controller/.test(s)) {
    if (DUALSENSE_EDGE.has(p) || s.includes('dualsense edge')) return out('playstation', 'DualSense Edge', 'playstation');
    if (DUALSENSE.has(p) || s.includes('dualsense')) return out('playstation', 'DualSense', 'playstation');
    if (DUALSHOCK4.has(p) || s.includes('dualshock 4') || s.includes('dualshock®4')) return out('playstation', 'DualShock 4', 'playstation');
    if (p === '0268' || s.includes('playstation(r)3') || s.includes('dualshock 3')) return out('playstation', 'DualShock 3', 'playstation');
    return out('playstation', 'PlayStation Controller', 'playstation');
  }
  if (vendor === NINTENDO || /joy-?con|pro controller|nintendo|switch/.test(s)) {
    if (p === '2006' || /joy-?con \(l\)/.test(s)) return out('nintendo', 'Joy-Con (L)', 'nintendo');
    if (p === '2007' || /joy-?con \(r\)/.test(s)) return out('nintendo', 'Joy-Con (R)', 'nintendo');
    if (p === '200e' || /joy-?con/.test(s)) return out('nintendo', 'Joy-Con (L/R)', 'nintendo');
    if (p === '2009' || p === '2069' || s.includes('pro controller')) return out('nintendo', 'Switch Pro Controller', 'nintendo');
    return out('nintendo', 'Nintendo Controller', 'nintendo');
  }
  if (vendor === VALVE || s.includes('steam')) {
    if (p === '1205' || s.includes('steam deck')) return out('xbox', 'Steam Deck', 'steam');
    if (p === '1102' || p === '1142' || s.includes('steam controller')) return out('generic', 'Steam Controller', 'steam');
    // Steam Input's virtual pad (28de:11ff) is an XInput device with Xbox labels.
    return out('xbox', 'Steam Virtual Gamepad', 'steam');
  }
  if (vendor === MICROSOFT || /xinput|xbox|x-box/.test(s)) {
    // Chrome names every XInput device "Xbox 360 Controller", so only trust real product ids.
    if (XBOX_SERIES.has(p)) return out('xbox', 'Xbox Series Controller', 'xbox');
    if (XBOX_ELITE.has(p) || s.includes('elite')) return out('xbox', 'Xbox Elite Controller', 'xbox');
    if (XBOX_360.has(p)) return out('xbox', 'Xbox 360 Controller', 'xbox');
    if (vendor === MICROSOFT && !/xbox|x-box|xinput/.test(s)) return out('generic', 'Microsoft Controller');
    return out('xbox', 'Xbox Controller', 'xbox');
  }
  if (vendor === LOGITECH) return out('xbox', LOGITECH_NAMES[p] ?? 'Logitech Gamepad');
  if (vendor === EIGHTBITDO || s.includes('8bitdo')) return out('generic', '8BitDo Controller');
  if (vendor && XINPUT_CLONES.has(vendor)) return out('xbox', 'Xbox-compatible Controller');
  return out('generic', productName(id) || 'Gamepad');
}

/**
 * Pads whose touchpad the browser can read (DualSense, DualSense Edge, DualShock 4, over WebHID:
 * touchpadHid.ts). The Steam Controller and Steam Deck trackpads belong to Steam Input.
 */
export function hasTouchpad(identity: PadIdentity): boolean {
  return identity.name === 'DualSense' || identity.name === 'DualSense Edge' || identity.name === 'DualShock 4';
}

/** Human product name from an unknown id ("Wireless Gamepad"), without browser decorations. */
function productName(id: string): string {
  return id
    .replace(/\(.*?(standard gamepad|vendor:).*?\)/i, '')
    .replace(/^\s*[0-9a-f]{1,4}-[0-9a-f]{1,4}-/i, '')
    .replace(/\s+/g, ' ')
    .trim()
    .slice(0, 40);
}
