import { logger } from '@librechat/data-schemas';

import type { Response } from 'express';
import type { ServerRequest } from '~/types';
import { createArchiveAllHandler, createSemindHistoryPolicy } from './archive';

jest.mock('@librechat/data-schemas', () => ({
  logger: {
    error: jest.fn(),
  },
}));

interface MockResponse {
  statusCode: number;
  body: { archivedCount: number } | string | undefined;
  status: jest.Mock;
  json: jest.Mock;
  send: jest.Mock;
}

function mockRequest(): ServerRequest {
  return {
    user: { id: 'user-123' },
  } as Partial<ServerRequest> as ServerRequest;
}

function mockResponse(): Response & MockResponse {
  const res: MockResponse = {
    statusCode: 200,
    body: undefined,
    status: jest.fn((statusCode: number) => {
      res.statusCode = statusCode;
      return res;
    }),
    json: jest.fn((body: MockResponse['body']) => {
      res.body = body;
      return res;
    }),
    send: jest.fn((body: MockResponse['body']) => {
      res.body = body;
      return res;
    }),
  };
  return res as Partial<Response> as Response & MockResponse;
}

describe('createArchiveAllHandler', () => {
  it('archives the authenticated user conversations and returns the result', async () => {
    const archiveAllConvos = jest.fn().mockResolvedValue({ archivedCount: 4 });
    const handler = createArchiveAllHandler({ archiveAllConvos });
    const res = mockResponse();

    await handler(mockRequest(), res);

    expect(archiveAllConvos).toHaveBeenCalledWith('user-123');
    expect(res.statusCode).toBe(200);
    expect(res.body).toEqual({ archivedCount: 4 });
  });

  it('logs and returns 500 when archiving fails', async () => {
    const error = new Error('Database error');
    const archiveAllConvos = jest.fn().mockRejectedValue(error);
    const handler = createArchiveAllHandler({ archiveAllConvos });
    const res = mockResponse();

    await handler(mockRequest(), res);

    expect(logger.error).toHaveBeenCalledWith('Error archiving all conversations', error);
    expect(res.statusCode).toBe(500);
    expect(res.body).toBe('Error archiving all conversations');
  });
});

describe('SE-mind permanent history', () => {
  function setup(path = '/') {
    const req = {
      user: { id: 'owner-a' },
      path,
      body: { arg: { conversationId: 'owned-chat' } },
      config: { config: { semind: { enabled: true } } },
    } as unknown as ServerRequest;
    const res = mockResponse();
    const deps = {
      archiveAllConvos: jest.fn().mockResolvedValue({ archivedCount: 2 }),
      saveConvo: jest.fn().mockResolvedValue({ conversationId: 'owned-chat', isArchived: true }),
    };
    const next = jest.fn();
    return { req, res, deps, next, middleware: createSemindHistoryPolicy(deps) };
  }

  it('archives only the authenticated owner without calling permanent deletion', async () => {
    const s = setup();
    await s.middleware(s.req, s.res, s.next);
    expect(s.deps.saveConvo).toHaveBeenCalledWith(
      expect.objectContaining({ userId: 'owner-a' }),
      { conversationId: 'owned-chat', isArchived: true },
      { noUpsert: true, preserveUpdatedAt: true },
    );
    expect(s.next).not.toHaveBeenCalled();
    expect(s.res.statusCode).toBe(200);
  });

  it('archives all for the authenticated owner', async () => {
    const s = setup('/all');
    await s.middleware(s.req, s.res, s.next);
    expect(s.deps.archiveAllConvos).toHaveBeenCalledWith('owner-a');
    expect(s.deps.saveConvo).not.toHaveBeenCalled();
    expect(s.next).not.toHaveBeenCalled();
  });

  it('does not create a history row or report success for a foreign id', async () => {
    const s = setup();
    s.deps.saveConvo.mockResolvedValue(null);
    await s.middleware(s.req, s.res, s.next);
    expect(s.res.statusCode).toBe(404);
    expect(s.next).not.toHaveBeenCalled();
  });

  it('fails closed if persistence cannot confirm archival', async () => {
    const s = setup();
    s.deps.saveConvo.mockResolvedValue({ message: 'Database unavailable' });
    await s.middleware(s.req, s.res, s.next);
    expect(s.res.statusCode).toBe(503);
    expect(s.next).not.toHaveBeenCalled();
  });
});
