﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Kompression.LempelZiv.Occurrence.Models;

namespace Kompression.LempelZiv.Matcher
{
    public interface ILzMatcher
    {
        IList<LzResult> FindMatches(Stream input);
    }
}