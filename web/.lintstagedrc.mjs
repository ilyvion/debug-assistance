const prettier = ['prettier --write'];

export default {
    '*.{js,mjs,cjs,ts,vue}': ['eslint --fix', ...prettier],
    '*.{json,css,html,md}': prettier,
};
