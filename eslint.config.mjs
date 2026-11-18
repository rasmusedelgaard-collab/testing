import js from '@eslint/js';
import globals from 'globals';
import tseslint from 'typescript-eslint';

const poolImportMessage =
  'src/db/pool.ts exposes rlsPool/systemPool. Only src/db/withRlsContext.ts and src/db/system.ts may import it. Use withRlsContext (RLS tables) or systemQuery (sessions, login upsert, public reads).';

export default tseslint.config(
  { ignores: ['dist/**', 'node_modules/**', 'coverage/**', 'src/public/**', 'scripts/copy-assets.mjs', 'scripts/package.mjs', 'deploy/**', 'package/**', 'dotnet/**'] },
  js.configs.recommended,
  ...tseslint.configs.recommended,
  {
    languageOptions: { globals: { ...globals.node } },
    rules: {
      '@typescript-eslint/no-unused-vars': ['error', { argsIgnorePattern: '^_', varsIgnorePattern: '^_' }],
      'no-restricted-imports': [
        'error',
        {
          patterns: [
            { group: ['**/db/pool', '**/pool', './pool'], message: poolImportMessage },
          ],
        },
      ],
    },
  },
  {
    // The only modules allowed to import the pool.
    files: ['src/db/withRlsContext.ts', 'src/db/system.ts', 'tests/**/*.ts'],
    rules: { 'no-restricted-imports': 'off' },
  },
  {
    // mssql (and raw SQL) stays inside src/db; services, routes and auth never see the driver.
    files: ['src/**/*.ts'],
    ignores: ['src/db/**'],
    rules: {
      'no-restricted-imports': [
        'error',
        {
          paths: [{ name: 'mssql', message: 'Import mssql only inside src/db/. Add a repository there.' }],
          patterns: [{ group: ['**/db/pool', '**/pool'], message: poolImportMessage }],
        },
      ],
    },
  },
  {
    files: ['src/public/**/*.js'],
    languageOptions: { globals: { ...globals.browser } },
  },
);
