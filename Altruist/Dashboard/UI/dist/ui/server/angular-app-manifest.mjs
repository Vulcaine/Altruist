
export default {
  bootstrap: () => import('./main.server.mjs').then(m => m.default),
  inlineCriticalCss: true,
  baseHref: '/altruist/dashboard/',
  locale: undefined,
  routes: undefined,
  entryPointToBrowserMapping: {},
  assets: {
    'index.csr.html': {size: 1031, hash: '556c1efde22a24fc29b4ef9e53fab1de90784c0998a564dddf510109ec4a2383', text: () => import('./assets-chunks/index_csr_html.mjs').then(m => m.default)},
    'index.server.html': {size: 1327, hash: '89e312a17fe5fc4720ecd3172feac745890657f8587fdfe4f43822b1b62b20b7', text: () => import('./assets-chunks/index_server_html.mjs').then(m => m.default)},
    'styles-GUDVE7EE.css': {size: 4103, hash: 'E20CZ5Z92MU', text: () => import('./assets-chunks/styles-GUDVE7EE_css.mjs').then(m => m.default)}
  },
};
