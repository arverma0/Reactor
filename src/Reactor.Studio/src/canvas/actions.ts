/**
 * Callbacks that canvas elements invoke but that belong to the surrounding mode (answering a question belongs to the
 * active run, opening a node belongs to the inspector). Modes install their handlers on mount.
 */
export const canvasActions = {
  answer: (_questionId: string, _value?: string, _text?: string): void => undefined,
  openNode: (_id: string): void => undefined,
  editNote: (_id: string, _text: string): void => undefined,
};
