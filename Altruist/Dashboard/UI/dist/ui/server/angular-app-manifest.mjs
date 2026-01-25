
export default {
  bootstrap: () => import('./main.server.mjs').then(m => m.default),
  inlineCriticalCss: true,
  baseHref: '/altruist/dashboard/',
  locale: undefined,
  routes: undefined,
  entryPointToBrowserMapping: {},
  assets: {
    'index.csr.html': {size: 1031, hash: '8043ddbab3638243f8dc3d4212897ea5998610cfe980a43a91ffbfb427d5b8b4', text: () => import('./assets-chunks/index_csr_html.mjs').then(m => m.default)},
    'index.server.html': {size: 1327, hash: '00c0470986908fa171c913b3f92a2d81fbd356fedc5be1aef57b301055a59a90', text: () => import('./assets-chunks/index_server_html.mjs').then(m => m.default)},
    'styles-CPKLBY45.css': {size: 4130, hash: '7+3rSzSVGtA', text: () => import('./assets-chunks/styles-CPKLBY45_css.mjs').then(m => m.default)}
  },
};
