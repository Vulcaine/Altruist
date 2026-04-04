
export default {
  bootstrap: () => import('./main.server.mjs').then(m => m.default),
  inlineCriticalCss: true,
  baseHref: '/altruist/dashboard/',
  locale: undefined,
  routes: undefined,
  entryPointToBrowserMapping: {},
  assets: {
    'index.csr.html': {size: 1031, hash: '6897aa1ba39d88620136f2961f51679d764939c5dbcd9cf85065b006d61330f9', text: () => import('./assets-chunks/index_csr_html.mjs').then(m => m.default)},
    'index.server.html': {size: 1327, hash: '43cdf920a9875c78b886f67d8f0e026195b77b4ccaa2276e8abcc52718b3f8b2', text: () => import('./assets-chunks/index_server_html.mjs').then(m => m.default)},
    'styles-CPKLBY45.css': {size: 4130, hash: '7+3rSzSVGtA', text: () => import('./assets-chunks/styles-CPKLBY45_css.mjs').then(m => m.default)}
  },
};
