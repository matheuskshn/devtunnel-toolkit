import { check, type State } from "./model.js";
import { privateDirectory, type Persistence } from "./state.js";

/** A candidate's private home is never written to the active credential row before promotion. */
export class StagedCredentials implements Persistence {
  private active = false;
  constructor(private readonly target: Persistence) { check(target.replaceCredentials, "CREDENTIAL_REPLACEMENT_UNSUPPORTED"); }
  async read(): Promise<State | undefined> { return undefined; }
  async write(): Promise<void> { throw new Error("Candidate cannot replace hub state"); }
  async restore(_id: string, home: string): Promise<void> { await privateDirectory(home); }
  async beginAuth(id: string): Promise<void> { if (this.active) await this.target.beginAuth(id); }
  async checkpoint(id: string, home: string): Promise<void> { if (this.active) await this.target.checkpoint(id, home); }
  async promote(id: string, home: string): Promise<void> {
    check(this.target.replaceCredentials, "CREDENTIAL_REPLACEMENT_UNSUPPORTED");
    await this.target.replaceCredentials(id, home);
    this.active = true;
  }
}
