#!/usr/bin/env node
'use strict';

const { run } = require('./launch.js');
const { parseUpdate, runUpdate } = require('./update.js');

const update = parseUpdate(process.argv.slice(2));
if (update) runUpdate(update);
else run('d365fo');
