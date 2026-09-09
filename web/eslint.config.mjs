import js from '@eslint/js';
import vuePrettierConfig from '@vue/eslint-config-prettier/skip-formatting';
import pluginVue from 'eslint-plugin-vue';
import globals from 'globals';
import tseslint from 'typescript-eslint';

const typedConfigs = [
    ...tseslint.configs.strictTypeChecked,
    ...tseslint.configs.stylisticTypeChecked,
];

const typedPlugins = Object.assign(
    {},
    ...typedConfigs.map((c) => c.plugins ?? {}),
);

const typedRules = Object.assign(
    {},
    ...typedConfigs.map((c) => c.rules ?? {}),
    {
        '@typescript-eslint/no-unused-vars': [
            'error',
            {
                args: 'all',
                argsIgnorePattern: '^_',
                caughtErrors: 'all',
                caughtErrorsIgnorePattern: '^_',
                destructuredArrayIgnorePattern: '^_',
                varsIgnorePattern: '^_',
                ignoreRestSiblings: true,
            },
        ],
        '@typescript-eslint/restrict-template-expressions': [
            'error',
            { allowNumber: true },
        ],
    },
);

export default tseslint.config(
    {
        ignores: ['../Site/**'],
    },

    js.configs.recommended,

    ...pluginVue.configs['flat/recommended'],
    vuePrettierConfig,

    // Type-checked linting for plain TS files.
    {
        files: ['**/*.ts'],
        languageOptions: {
            parser: tseslint.parser,
            parserOptions: {
                projectService: {
                    defaultProject: './tsconfig.app.json',
                },
                tsconfigRootDir: import.meta.dirname,
            },
            globals: globals.browser,
        },
        plugins: typedPlugins,
        rules: typedRules,
    },

    // Vue SFCs keep vue-eslint-parser as the top-level parser (set by
    // flat/recommended above) and only delegate the <script> block to
    // typescript-eslint's parser, so this must not reassign languageOptions.parser.
    {
        files: ['**/*.vue'],
        languageOptions: {
            parserOptions: {
                parser: tseslint.parser,
                extraFileExtensions: ['.vue'],
                projectService: {
                    defaultProject: './tsconfig.app.json',
                },
                tsconfigRootDir: import.meta.dirname,
            },
            globals: globals.browser,
        },
        plugins: typedPlugins,
        rules: typedRules,
    },

    {
        files: ['*.config.{js,mjs,ts}', '.lintstagedrc.mjs'],
        languageOptions: {
            globals: globals.node,
        },
    },
);
