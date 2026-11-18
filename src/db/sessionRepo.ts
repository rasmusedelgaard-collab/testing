import sql from 'mssql';
import { systemQuery } from './system';

/** SQL for app.Sessions (ADR-0004). Runs on systemPool: sessions are read before any identity exists. */
export interface SessionRepo {
  get(sid: string): Promise<string | null>;
  set(sid: string, data: string, expiresAt: Date): Promise<void>;
  touch(sid: string, expiresAt: Date): Promise<void>;
  destroy(sid: string): Promise<void>;
  /** Deletes up to `batch` expired rows; returns how many were removed. */
  deleteExpired(batch: number): Promise<number>;
}

export const sessionRepo: SessionRepo = {
  async get(sid) {
    const rows = await systemQuery<{ Data: string }>(
      'SELECT Data FROM app.Sessions WHERE Sid = @sid AND ExpiresAt > SYSUTCDATETIME()',
      [{ name: 'sid', type: sql.VarChar(128), value: sid }],
    );
    return rows[0]?.Data ?? null;
  },
  async set(sid, data, expiresAt) {
    await systemQuery(
      `MERGE app.Sessions WITH (HOLDLOCK) AS s
       USING (SELECT @sid AS Sid) AS src ON s.Sid = src.Sid
       WHEN MATCHED THEN UPDATE SET Data = @data, ExpiresAt = @exp
       WHEN NOT MATCHED THEN INSERT (Sid, Data, ExpiresAt) VALUES (@sid, @data, @exp);`,
      [
        { name: 'sid', type: sql.VarChar(128), value: sid },
        { name: 'data', type: sql.NVarChar(sql.MAX), value: data },
        { name: 'exp', type: sql.DateTime2(0), value: expiresAt },
      ],
    );
  },
  async touch(sid, expiresAt) {
    await systemQuery('UPDATE app.Sessions SET ExpiresAt = @exp WHERE Sid = @sid', [
      { name: 'sid', type: sql.VarChar(128), value: sid },
      { name: 'exp', type: sql.DateTime2(0), value: expiresAt },
    ]);
  },
  async destroy(sid) {
    await systemQuery('DELETE FROM app.Sessions WHERE Sid = @sid', [{ name: 'sid', type: sql.VarChar(128), value: sid }]);
  },
  async deleteExpired(batch) {
    const rows = await systemQuery<{ n: number }>(
      `DELETE TOP (@batch) FROM app.Sessions WHERE ExpiresAt < SYSUTCDATETIME();
       SELECT @@ROWCOUNT AS n;`,
      [{ name: 'batch', type: sql.Int, value: batch }],
    );
    return rows[0]?.n ?? 0;
  },
};
