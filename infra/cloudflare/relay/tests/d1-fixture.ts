import { DatabaseSync, type SQLInputValue } from 'node:sqlite';
import { readFileSync } from 'node:fs';

/** SQL の文字列分岐を模倣せず、実 SQLite の制約・transaction を通す D1 fixture。 */
export class SqliteD1 {
  readonly sqlite = new DatabaseSync(':memory:');
  constructor(ready = true) {
    this.sqlite.exec(readFileSync(new URL('../schema.sql', import.meta.url), 'utf8'));
    if (ready) this.sqlite.exec("UPDATE auth_key_migration SET phase='ready' WHERE singleton=1");
  }
  prepare(sql: string) {
    const create = (args: SQLInputValue[]) => {
      const statement = this.sqlite.prepare(sql);
      const execute = () => {
        if (/^\s*SELECT/i.test(sql)) return { success: true, results: statement.all(...args), meta: { changes: 0 } };
        const result = statement.run(...args);
        return { success: true, results: [], meta: { changes: Number(result.changes) } };
      };
      return {
        bind: (...values: unknown[]) => create(values as SQLInputValue[]),
        first: async <T>() => (statement.get(...args) ?? null) as T | null,
        all: async () => execute(),
        run: async () => execute(),
        execute,
      };
    };
    return create([]);
  }
  async batch(statements: Array<{ execute: () => unknown }>) {
    this.sqlite.exec('BEGIN');
    try {
      const results = statements.map((statement) => statement.execute());
      this.sqlite.exec('COMMIT');
      return results;
    } catch (error) {
      this.sqlite.exec('ROLLBACK');
      throw error;
    }
  }
  seedKey(deviceId: string, publicKey: string) {
    this.sqlite.prepare('INSERT INTO device_key_bindings VALUES (?,?,?)').run(deviceId, publicKey, Date.now());
  }
}
